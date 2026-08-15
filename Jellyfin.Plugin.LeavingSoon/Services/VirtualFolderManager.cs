using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
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
    private readonly ILogger<VirtualFolderManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VirtualFolderManager"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="libraryMonitor">The library monitor, used for scoped (path-level) refreshes.</param>
    /// <param name="logger">The logger.</param>
    public VirtualFolderManager(
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        ILogger<VirtualFolderManager> logger)
    {
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
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
}
