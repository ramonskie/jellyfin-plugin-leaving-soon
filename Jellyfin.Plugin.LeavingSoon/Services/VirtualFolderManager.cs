using System;
using System.Linq;
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
    private readonly ILogger<VirtualFolderManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="VirtualFolderManager"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public VirtualFolderManager(ILibraryManager libraryManager, ILogger<VirtualFolderManager> logger)
    {
        _libraryManager = libraryManager;
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
    /// path) when needed.
    /// </summary>
    /// <param name="name">The library name.</param>
    /// <param name="collectionType">The Jellyfin collection type ("movies" or "tvshows").</param>
    /// <param name="path">The host directory path the library points at.</param>
    /// <returns>The item id of the virtual folder, or null when not resolvable.</returns>
    public async Task<string?> EnsureVirtualFolderAsync(string name, string collectionType, string path)
    {
        var existing = GetVirtualFolder(name);
        if (existing != null)
        {
            if (!existing.Locations.Any(l => string.Equals(l, path, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogInformation("Adding path {Path} to existing library {Name}", path, name);
                _libraryManager.AddMediaPath(name, new MediaPathInfo(path));
            }

            return existing.ItemId;
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
        return GetVirtualFolder(name)?.ItemId;
    }

    /// <summary>
    /// Deletes a virtual folder by name and triggers a library refresh.
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
    }

    /// <summary>
    /// Triggers a global library scan. Jellyfin exposes this specifically so plugins
    /// can trigger a scan after changing the library structure.
    /// </summary>
    public void RefreshLibrary()
    {
        _logger.LogDebug("Triggering global library scan");
        _libraryManager.QueueLibraryScan();
    }

    /// <summary>
    /// After deleting an empty library, Jellyfin needs two refreshes ~5s apart to update
    /// user views (/Users/{userId}/Views). This replicates the empirically-confirmed
    /// behavior from OxiCleanarr.
    /// </summary>
    /// <returns>A task representing the double refresh.</returns>
    public async Task DoubleRefreshAsync()
    {
        _logger.LogInformation("Triggering double library refresh to update user view cache");
        RefreshLibrary();
        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        RefreshLibrary();
    }
}
