using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LeavingSoon.Services;

/// <summary>
/// Manages Jellyfin virtual folders (libraries) for the leaving-soon libraries.
/// Runs in-process via ILibraryManager, so no separate Jellyfin HTTP client is needed.
/// </summary>
public class VirtualFolderManager
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<VirtualFolderManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VirtualFolderManager"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="libraryMonitor">The library monitor, used for scoped (path-level) refreshes.</param>
    /// <param name="fileSystem">The file system, used to build directory services for metadata refreshes.</param>
    /// <param name="logger">The logger.</param>
    public VirtualFolderManager(
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        IFileSystem fileSystem,
        ILogger<VirtualFolderManager> logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <summary>
    /// Gets a virtual folder by name.
    /// </summary>
    /// <param name="name">The library name.</param>
    /// <returns>The virtual folder, or null when it does not exist.</returns>
    public VirtualFolderInfo? GetVirtualFolder(string name)
    {
        return _libraryManager.GetVirtualFolders()
            .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets a value indicating whether a virtual folder is currently enabled (visible in
    /// user views). Used to detect the empty→refill transition so the cover can be
    /// force-regenerated on re-enable. Mirrors the enabled-state read in
    /// <see cref="SetLibraryEnabledAsync"/>.
    /// </summary>
    /// <param name="name">The library name.</param>
    /// <returns>True when the library exists and is enabled; false when it is missing or disabled.</returns>
    public bool IsLibraryEnabled(string name)
    {
        var virtualFolder = GetVirtualFolder(name);
        if (virtualFolder == null || !Guid.TryParse(virtualFolder.ItemId, out var itemId))
        {
            return false;
        }

        var folder = _libraryManager.GetItemById<CollectionFolder>(itemId);
        return folder?.GetLibraryOptions().Enabled ?? false;
    }

    /// <summary>
    /// Ensures a virtual folder exists with the given path, creating it (or adding the
    /// path) when needed. AddVirtualFolder internally runs <see cref="ILibraryManager.ValidateTopLibraryFolders"/>
    /// so the new library appears in user views without an additional scan.
    /// </summary>
    /// <param name="name">The library name.</param>
    /// <param name="collectionType">The Jellyfin collection type ("movies" or "tvshows").</param>
    /// <param name="path">The host directory path the library points at.</param>
    /// <returns>The item id of the virtual folder, and whether the path was newly added
    /// (library created or path just registered) and therefore needs an initial scan to
    /// materialize the physical folder at the path.</returns>
    public async Task<(string? ItemId, bool NeedsInitialScan)> EnsureVirtualFolderAsync(string name, string collectionType, string path)
    {
        var existing = GetVirtualFolder(name);
        if (existing != null)
        {
            if (!existing.Locations.Any(l => string.Equals(l, path, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation("Adding path {Path} to existing library {Name}", path, name);
                _libraryManager.AddMediaPath(name, new MediaPathInfo(path));
                return (existing.ItemId, true);
            }

            return (existing.ItemId, false);
        }

        _logger.LogInformation("Creating virtual folder {Name} ({CollectionType}) at {Path}", name, collectionType, path);
        var options = new LibraryOptions
        {
            PathInfos = [new MediaPathInfo(path)],
            SaveLocalMetadata = true,
        };
        var collectionTypeOptions = collectionType.Equals("tvshows", StringComparison.OrdinalIgnoreCase)
            ? CollectionTypeOptions.tvshows
            : CollectionTypeOptions.movies;

        await _libraryManager.AddVirtualFolder(name, collectionTypeOptions, options, false).ConfigureAwait(false);
        return (GetVirtualFolder(name)?.ItemId, true);
    }

    /// <summary>
    /// Enables or disables a virtual folder. Disabling hides the library from all user
    /// views while keeping it, its metadata rows and its symlinks intact — CollectionFolder
    /// <c>IsVisible</c> returns false whenever <c>LibraryOptions.Enabled</c> is false, and
    /// reads the options live, so the toggle takes effect immediately and re-enabling is
    /// instant with no rescan. This is the same toggle the dashboard Library Settings
    /// exposes via its "Enable the library" option.
    /// </summary>
    /// <param name="name">The library name.</param>
    /// <param name="enabled">Whether the library should be visible in user views.</param>
    /// <returns>A completed task.</returns>
    public Task SetLibraryEnabledAsync(string name, bool enabled)
    {
        var virtualFolder = GetVirtualFolder(name);
        if (virtualFolder == null)
        {
            _logger.LogDebug("Virtual folder {Name} does not exist, nothing to toggle", name);
            return Task.CompletedTask;
        }

        if (!Guid.TryParse(virtualFolder.ItemId, out var itemId))
        {
            _logger.LogWarning("Virtual folder {Name} has an invalid item id, cannot toggle enabled state", name);
            return Task.CompletedTask;
        }

        var folder = _libraryManager.GetItemById<CollectionFolder>(itemId);
        if (folder == null)
        {
            _logger.LogWarning("Collection folder item {ItemId} for {Name} was not found", itemId, name);
            return Task.CompletedTask;
        }

        try
        {
            var options = folder.GetLibraryOptions();
            if (options.Enabled == enabled)
            {
                _logger.LogDebug("Virtual folder {Name} is already {State}", name, enabled ? "enabled" : "disabled");
                return Task.CompletedTask;
            }

            options.Enabled = enabled;
            folder.UpdateLibraryOptions(options);
            _logger.LogInformation("Virtual folder {Name} {State}", name, enabled ? "enabled" : "disabled");
        }
        catch (Exception ex)
        {
            // Never let a toggle failure abort the sync run. CollectionFolder.SaveLibraryOptions
            // applies the in-memory options first, so a failed options.xml write still takes effect
            // for the running server; the change reverts after a restart, and the next sync then
            // re-applies it (the reloaded cached value no longer matches).
            _logger.LogWarning(ex, "Failed to {Action} virtual folder {Name}", enabled ? "enable" : "disable", name);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes a virtual folder by name and rebuilds the top-level library folders so
    /// the removed library disappears from user views. <see cref="ILibraryManager.RemoveVirtualFolder"/>
    /// alone (with refreshLibrary=false) leaves the cached root children stale; calling
    /// <see cref="ILibraryManager.ValidateTopLibraryFolders"/> with removeRoot=true purges the
    /// orphaned collection folder without a full library scan.
    /// </summary>
    /// <param name="name">The library name.</param>
    /// <param name="ownedPath">The directory a plugin-created library exclusively points at.
    /// Deletion only proceeds when every location resolves under this path. Removing a real
    /// (admin-created) library is avoided even when the plugin merged its symlink path into
    /// it (see <see cref="EnsureVirtualFolderAsync"/>): such a library also holds locations
    /// outside this path, and the whole virtual folder (all locations) would be deleted and
    /// its metadata rows cascade-purged, so any unowned location aborts the delete.</param>
    /// <returns>A task representing the operation.</returns>
    public async Task DeleteVirtualFolderAsync(string name, string ownedPath)
    {
        var virtualFolder = GetVirtualFolder(name);
        if (virtualFolder == null)
        {
            _logger.LogDebug("Virtual folder {Name} does not exist, nothing to delete", name);
            return;
        }

        if (!virtualFolder.Locations.All(location => IsOwnedLocation(location, ownedPath)))
        {
            _logger.LogWarning(
                "Skipping deletion of virtual folder {Name}: not every location points under {OwnedPath} " +
                "(name collision with, or path merged into, a library this plugin does not own)",
                name,
                ownedPath);
            return;
        }

        // Disable the library before removing it so it drops out of user views instantly
        // (CollectionFolder IsVisible reads LibraryOptions.Enabled live) and stays hidden
        // even if a later removal step fails. Cosmetic only — the purge below is what
        // actually removes the library. Never makes real progress on failure, and the
        // library is already confirmed owned, so this is safe.
        await SetLibraryEnabledAsync(name, false).ConfigureAwait(false);

        Guid? itemId = Guid.TryParse(virtualFolder.ItemId, out var parsed) ? parsed : null;

        _logger.LogInformation("Deleting virtual folder {Name}", name);

        // RemoveVirtualFolder throws a FileNotFoundException when the collection-folder
        // directory at <data>/root/default/<name> is already gone. That must not abort the
        // cleanup of the other (Tv) library, so isolate it: the config entry is still
        // dropped and the orphaned item purge below still runs from the item id.
        try
        {
            await _libraryManager.RemoveVirtualFolder(name, false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RemoveVirtualFolder reported an error for {Name}, continuing with item purge", name);
        }

        // RemoveVirtualFolder only drops the config entry and the collection folder
        // directory. The orphaned CollectionFolder item can survive revalidation and
        // keep materializing in user views, so purge the database item explicitly —
        // the same path the dashboard's DELETE /Items/{id} takes. This must happen
        // before ValidateTopLibraryFolders rebuilds the root folder children, or the
        // ghost library stays visible in user views.
        //
        // DeleteItem cascade-removes the folder's child baseitem rows too, wiping the
        // leaving-soon library's metadata index and its (duplicate-of-the-real-library)
        // playstate. That is intentional: the rows only exist because Jellyfin scanned
        // the symlinked media, no files are touched (DeleteFileLocation=false), and it
        // is the same operation the dashboard performs. The guard is the name contract:
        // DeleteVirtualFolderAsync is only ever called with MoviesLibraryName or
        // TvLibraryName, which the plugin owns.
        if (itemId.HasValue)
        {
            try
            {
                var item = _libraryManager.GetItemById(itemId.Value);
                if (item is CollectionFolder)
                {
                    _logger.LogInformation("Purging orphaned collection folder item {ItemId} for {Name}", itemId.Value, name);
                    _libraryManager.DeleteItem(item, new DeleteOptions(), true);
                }
                else
                {
                    _logger.LogDebug("No orphaned collection folder item to purge for {Name}", name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to purge collection folder item for {Name}", name);
            }
        }

        // The delete has already committed, so don't let sync cancellation strand a
        // stale view here — revalidate with CancellationToken.None.
        try
        {
            await _libraryManager.ValidateTopLibraryFolders(CancellationToken.None, true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to revalidate top-level library folders after deleting {Name}", name);
        }
    }

    /// <summary>
    /// Triggers a global library scan. Needed when a brand-new leaving-soon library is
    /// created: the physical folder at the symlink path is only materialized by a scan,
    /// and until then a scoped path refresh has nothing to resolve to. Once that physical
    /// folder exists, subsequent syncs use <see cref="QuickRefreshPath"/> instead.
    /// </summary>
    public void RefreshLibrary()
    {
        _logger.LogDebug("Triggering global library scan");
        _libraryManager.QueueLibraryScan();
    }

    /// <summary>
    /// Triggers a scoped refresh of a single path (e.g. the symlink directory of an
    /// already-existing leaving-soon library). Jellyfin's library monitor debounces
    /// the change and re-validates only that folder, so added/removed symlinks are
    /// picked up without a full library scan. This is fire-and-forget — it can
    /// silently no-op if no indexed item resolves at the path — so it is logged at
    /// Info level for observability.
    /// </summary>
    /// <param name="path">The directory whose contents changed.</param>
    public void QuickRefreshPath(string path)
    {
        _logger.LogInformation("Triggering scoped library refresh for {Path}", path);
        _libraryMonitor.ReportFileSystemChanged(path);
    }

    /// <summary>
    /// Regenerates a leaving-soon library's cover after its items change. Jellyfin derives
    /// a library's primary image from a dynamic collage (<c>CollectionFolderImageProvider</c>):
    /// up to 8 random items with primary images are composited into a thumbnail, and that
    /// collage is only regenerated when the CollectionFolder's own metadata is refreshed —
    /// and only when items with primary images exist at that moment. A scoped path refresh
    /// (or even Jellyfin's own first-scan ordering) never re-refreshes the CollectionFolder
    /// after items are indexed, so a fresh library shows no cover until a later full scan.
    /// </summary>
    /// <remarks>
    /// This waits for the just-triggered scan to index at least one item with a primary image,
    /// then regenerates the cover. When <paramref name="forceRefresh"/> is false the gate is a
    /// null image: the collage only needs generating while no cover exists yet (once a cover
    /// exists, Jellyfin's <c>HasChanged</c> gate considers it unchanged and will not regenerate
    /// it). When true (a library being re-enabled after the empty period) the cover is
    /// force-regenerated from the current items, so the empty→refill cycle doesn't leave a
    /// stale collage of the previous leaving set.
    /// </remarks>
    /// <param name="libraryName">The leaving-soon library name.</param>
    /// <param name="forceRefresh">Whether to regenerate the cover even when one already exists
    /// (re-enable after empty), bypassing the image refresh gate.</param>
    /// <param name="timeout">How long to wait for the scan to index items with images.</param>
    /// <param name="cancellationToken">Cancellation token (e.g. from the sync), so a cancelled
    /// sync can abort the wait instead of holding the lock until the timeout elapses.</param>
    /// <returns>A task representing the operation.</returns>
    public async Task RefreshLibraryImageAsync(
        string libraryName,
        bool forceRefresh,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var virtualFolder = GetVirtualFolder(libraryName);
            if (virtualFolder == null || !Guid.TryParse(virtualFolder.ItemId, out var itemId))
            {
                _logger.LogDebug("Virtual folder {Name} not found; skipping cover refresh", libraryName);
                return;
            }

            var collectionFolder = _libraryManager.GetItemById<CollectionFolder>(itemId);
            if (collectionFolder == null)
            {
                _logger.LogDebug("Collection folder {ItemId} for {Name} not found; skipping cover refresh", itemId, libraryName);
                return;
            }

            // The dynamic collage only needs regenerating while the cover is still missing —
            // unless the caller forces it (re-enable after empty: the stale collage shows the
            // previous leaving set, which is the wrong cover for a churn-driven library).
            if (!forceRefresh && collectionFolder.HasImage(ImageType.Primary, 0))
            {
                _logger.LogDebug("Virtual folder {Name} already has a cover; skipping cover refresh", libraryName);
                return;
            }

            // Wait for the scan to index items with images. Bounded: if the initial scan of a
            // large library is slow, skip this run — the next sync finds the items already
            // indexed and generates the cover then.
            var itemsIndexed = await WaitForConditionAsync(
                    () => HasIndexedItemsWithImages(collectionFolder),
                    timeout,
                    TimeSpan.FromSeconds(1),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!itemsIndexed)
            {
                _logger.LogWarning(
                    "Timed out waiting for {Name} to index items with images; cover will be generated by a later sync",
                    libraryName);
                return;
            }

            if (forceRefresh)
            {
                // Regenerate the collage from the current leaving set. The image gate is
                // separate from the metadata gate: MetadataRefreshMode.FullRefresh only forces
                // metadata providers, while image providers run when ImageRefreshMode is
                // FullRefresh (which also marks the primary image as replaced so the old
                // collage file is overwritten). An existing collage otherwise never regenerates
                // (its stored DateModified always matches the file mtime).
                _logger.LogInformation("Forcing cover regeneration for {Name}", libraryName);
                await collectionFolder
                    .RefreshMetadata(
                        new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                        {
                            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                            ReplaceAllImages = true,
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation("Refreshing {Name} metadata so its cover collage regenerates", libraryName);
                collectionFolder.ChangedExternally();
            }
        }
        catch (Exception ex)
        {
            // Cover regeneration is best-effort housekeeping; it must never break the sync
            // or the library re-enable that follows it.
            _logger.LogWarning(ex, "Failed to refresh cover for {Name}", libraryName);
        }
    }

    /// <summary>
    /// Determines whether a virtual-folder location points under the directory this plugin
    /// manages (the base path by sub-directory). Used as the ownership guard before any
    /// destructive removal: a plugin-created library always holds a location under this
    /// path, while an admin library colliding with a configured library name never does.
    /// </summary>
    /// <param name="location">A single location of the virtual folder.</param>
    /// <param name="ownedPath">The fully-prefixed directory the location must resolve under.</param>
    /// <returns>True when the location is at or under <paramref name="ownedPath"/>. An empty
    /// <paramref name="ownedPath"/> (misconfigured base path) is never owned.</returns>
    internal static bool IsOwnedLocation(string location, string ownedPath)
    {
        if (string.IsNullOrWhiteSpace(ownedPath))
        {
            // A blank owned path means every path would match; that is a misconfiguration
            // (empty BasePath), not ownership. Fail closed so uninstall deletes nothing.
            return false;
        }

        // Linux/macOS filesystems are case-sensitive, Windows is not. Match the semantics
        // of the underlying filesystem so a case-variant real library is not treated as owned.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!location.StartsWith(ownedPath, comparison))
        {
            return false;
        }

        // Ensure a real path-boundary match: <owned>/x belongs, <owned>-sibling does not.
        return location.Length == ownedPath.Length || location[ownedPath.Length] == Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it returns true, <paramref name="timeout"/>
    /// elapses, or the token is cancelled. Used to wait for the library scan to index items
    /// without blocking indefinitely.
    /// </summary>
    /// <param name="condition">The condition to poll.</param>
    /// <param name="timeout">Total time budget.</param>
    /// <param name="pollInterval">Delay between polls.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the condition became true within the budget, otherwise false.</returns>
    internal static async Task<bool> WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Gets the item kinds a <c>CollectionFolderImageProvider</c> cover collage draws
    /// from for a given collection type. Mirrors the provider's own switch so the "has items
    /// with images" precondition uses the same item set the collage will use.
    /// </summary>
    /// <param name="collectionType">The collection folder's collection type.</param>
    /// <returns>The item kinds whose primary images feed the collage.</returns>
    internal static BaseItemKind[] GetIncludeItemTypes(CollectionType? collectionType)
    {
        // The plugin only creates movies and tvshows libraries (see
        // EnsureVirtualFolderAsync); the provider's other collection types are not reachable.
        return collectionType switch
        {
            CollectionType.movies => [BaseItemKind.Movie],
            CollectionType.tvshows => [BaseItemKind.Series],
            _ => [BaseItemKind.Video, BaseItemKind.Audio, BaseItemKind.Photo, BaseItemKind.Movie, BaseItemKind.Series],
        };
    }

    /// <summary>
    /// Determines whether the collection folder already indexes at least one item with a
    /// primary image — the precondition for <c>CollectionFolderImageProvider</c> to build a
    /// cover collage. Mirrors the provider's own query (recursive, matching item kinds,
    /// items with primary images).
    /// </summary>
    /// <param name="collectionFolder">The collection folder.</param>
    /// <returns>True when at least one indexed item has a primary image.</returns>
    private static bool HasIndexedItemsWithImages(CollectionFolder collectionFolder)
    {
        return collectionFolder.GetItemList(new InternalItemsQuery
        {
            CollapseBoxSetItems = false,
            Recursive = true,
            DtoOptions = new DtoOptions(false),
            ImageTypes = [ImageType.Primary],
            IncludeItemTypes = GetIncludeItemTypes(collectionFolder.CollectionType),
            Limit = 1,
        }).Count > 0;
    }
}
