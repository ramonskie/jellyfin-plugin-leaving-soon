using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
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
    /// Deletes a virtual folder by name and rebuilds the top-level library folders so
    /// the removed library disappears from user views. <see cref="ILibraryManager.RemoveVirtualFolder"/>
    /// alone (with refreshLibrary=false) leaves the cached root children stale; calling
    /// <see cref="ILibraryManager.ValidateTopLibraryFolders"/> with removeRoot=true purges the
    /// orphaned collection folder without a full library scan.
    /// </summary>
    /// <param name="name">The library name.</param>
    /// <returns>A task representing the operation.</returns>
    public async Task DeleteVirtualFolderAsync(string name)
    {
        if (GetVirtualFolder(name) == null)
        {
            _logger.LogDebug("Virtual folder {Name} does not exist, nothing to delete", name);
            return;
        }

        _logger.LogInformation("Deleting virtual folder {Name}", name);
        await _libraryManager.RemoveVirtualFolder(name, false).ConfigureAwait(false);

        // The delete has already committed, so don't let sync cancellation strand a
        // stale view here — revalidate with CancellationToken.None. If revalidation
        // fails, log and move on: the orphaned collection folder is harmless and a
        // future sync will rebuild the library from scratch anyway.
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
}
