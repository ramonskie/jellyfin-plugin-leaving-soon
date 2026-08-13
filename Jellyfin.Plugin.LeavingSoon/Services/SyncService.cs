using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeavingSoon.Configuration;
using Jellyfin.Plugin.LeavingSoon.Providers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LeavingSoon.Services;

/// <summary>
/// Polls all configured providers and reconciles the leaving-soon symlink libraries.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "Singleton registered in the DI container; the sync semaphore lives for the process lifetime.")]
public class SyncService : IScheduledTask
{
    private const string MoviesSubDir = "movies";
    private const string TvSubDir = "tv";

    private readonly ProviderRegistry _providerRegistry;
    private readonly SymlinkManager _symlinkManager;
    private readonly VirtualFolderManager _virtualFolderManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SyncService> _logger;

    // Serializes sync executions so overlapping manual/scheduled syncs can't race
    // the symlink reconciliation (e.g. an exclude + re-include pair).
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private int _consecutiveProviderFailures;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncService"/> class.
    /// </summary>
    /// <param name="providerRegistry">The provider registry.</param>
    /// <param name="symlinkManager">The symlink manager.</param>
    /// <param name="virtualFolderManager">The virtual folder manager.</param>
    /// <param name="libraryManager">The library manager (for path resolution).</param>
    /// <param name="logger">The logger.</param>
    public SyncService(
        ProviderRegistry providerRegistry,
        SymlinkManager symlinkManager,
        VirtualFolderManager virtualFolderManager,
        ILibraryManager libraryManager,
        ILogger<SyncService> logger)
    {
        _providerRegistry = providerRegistry;
        _symlinkManager = symlinkManager;
        _virtualFolderManager = virtualFolderManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Sync Leaving Soon libraries";

    /// <inheritdoc />
    public string Key => "LeavingSoonSync";

    /// <inheritdoc />
    public string Description => "Polls the configured leaving-soon providers and reconciles the symlink libraries.";

    /// <inheritdoc />
    public string Category => "Leaving Soon";

    /// <summary>
    /// Resolves the path to symlink for an item. Movies resolve to the media file;
    /// the containing folder is used instead so the leaving-soon library shows the
    /// whole movie folder, consistent with shows (which resolve to their series
    /// folder and contain Season subdirectories). Only the folder is used when it is
    /// a dedicated movie folder (no subdirectories) — a flat movie in a library root
    /// falls back to a file symlink to avoid symlinking the entire library.
    /// </summary>
    /// <param name="sourcePath">The Jellyfin-resolved item path.</param>
    /// <returns>The path to symlink (a folder for movies in their own folder, otherwise unchanged).</returns>
    public static string ResolveLinkSource(string sourcePath)
    {
        if (File.Exists(sourcePath))
        {
            var movieDir = Path.GetDirectoryName(sourcePath);
            if (!string.IsNullOrEmpty(movieDir) && Directory.GetDirectories(movieDir).Length == 0)
            {
                return movieDir;
            }
        }

        return sourcePath;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var intervalMinutes = Plugin.Instance?.Configuration.SyncIntervalMinutes ?? 15;
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(intervalMinutes).Ticks,
            }
        ];
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteCoreAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task ExecuteCoreAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.LogWarning("Plugin configuration not available, skipping sync");
            return;
        }

        progress.Report(5);

        var (items, failureCount) = await _providerRegistry
            .CollectItemsAsync(config, cancellationToken)
            .ConfigureAwait(false);

        if (failureCount > 0)
        {
            _consecutiveProviderFailures++;
        }
        else
        {
            _consecutiveProviderFailures = 0;
        }

        if (_consecutiveProviderFailures >= config.ForceEmptyAfterFailureCount && items.Count == 0)
        {
            _logger.LogWarning(
                "Ignoring empty result: {Failures} consecutive provider failures and 0 items - treating as provider outage, not an empty library",
                _consecutiveProviderFailures);
            return;
        }

        // Resolve the on-disk path for each item from Jellyfin's own metadata.
        var itemsWithPath = new List<LeavingSoonItem>();
        foreach (var item in items)
        {
            var path = ResolvePath(item);
            if (string.IsNullOrWhiteSpace(path))
            {
                _logger.LogDebug("Skipping item {Id} - could not resolve a Jellyfin path", item.MediaServerId);
                continue;
            }

            item.SourcePath = path;
            itemsWithPath.Add(item);
        }

        progress.Report(40);

        var movies = itemsWithPath.Where(i => !i.Type.Equals("show", StringComparison.OrdinalIgnoreCase)).ToList();
        var tv = itemsWithPath.Where(i => i.Type.Equals("show", StringComparison.OrdinalIgnoreCase)).ToList();

        _logger.LogInformation(
            "Reconciling leaving-soon libraries: {MovieCount} movies, {TvCount} shows",
            movies.Count,
            tv.Count);

        await SyncLibraryAsync(
            config,
            config.MoviesLibraryName,
            MoviesSubDir,
            movies,
            cancellationToken).ConfigureAwait(false);

        progress.Report(70);

        await SyncLibraryAsync(
            config,
            config.TvLibraryName,
            TvSubDir,
            tv,
            cancellationToken).ConfigureAwait(false);

        progress.Report(100);
    }

    private async Task SyncLibraryAsync(
        PluginConfiguration config,
        string libraryName,
        string subDir,
        List<LeavingSoonItem> items,
        CancellationToken cancellationToken)
    {
        var symlinkDir = Path.Combine(config.BasePath, subDir);
        var collectionType = subDir == TvSubDir ? "tvshows" : "movies";

        // Hide-when-empty: clean symlinks, delete the virtual folder, double-refresh.
        if (items.Count == 0)
        {
            if (!config.HideWhenEmpty)
            {
                _logger.LogDebug("Library {Name} is empty but hide_when_empty is false, leaving it", libraryName);
                return;
            }

            var existing = _virtualFolderManager.GetVirtualFolder(libraryName);
            if (existing == null)
            {
                _logger.LogDebug("Library {Name} already absent, nothing to do", libraryName);
                return;
            }

            _logger.LogInformation("Library {Name} is empty - cleaning up (hide_when_empty)", libraryName);
            CleanupAllSymlinks(symlinkDir);
            await _virtualFolderManager.DeleteVirtualFolderAsync(libraryName).ConfigureAwait(false);
            await _virtualFolderManager.DoubleRefreshAsync().ConfigureAwait(false);
            return;
        }

        // Ensure the host directory and virtual folder exist.
        _symlinkManager.EnsureDirectoryExists(symlinkDir);
        var libraryItemId = await _virtualFolderManager
            .EnsureVirtualFolderAsync(libraryName, collectionType, symlinkDir)
            .ConfigureAwait(false);

        // Create symlinks for items that are not yet linked.
        var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.SourcePath == null)
            {
                continue;
            }

            var sourcePath = ResolveLinkSource(item.SourcePath);

            var fileName = Path.GetFileName(sourcePath);
            desired[fileName] = Path.Combine(symlinkDir, fileName);
            if (!File.Exists(desired[fileName]) && !Directory.Exists(desired[fileName]))
            {
                try
                {
                    await _symlinkManager
                        .CreateSymlinkAsync(sourcePath, symlinkDir, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to create symlink for {Source}", sourcePath);
                }
            }
        }

        // Remove stale symlinks (present on disk but not in the desired set).
        var existingSymlinks = _symlinkManager.ListSymlinks(symlinkDir);
        foreach (var link in existingSymlinks)
        {
            if (!desired.ContainsKey(link.Name))
            {
                try
                {
                    _symlinkManager.RemoveSymlink(link.Path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove stale symlink {Path}", link.Path);
                }
            }
        }

        // Refresh so newly-created symlinks show up. libraryItemId is captured for
        // future scoped-refresh use; global refresh is reliable for both cases today.
        _ = libraryItemId;
        _virtualFolderManager.RefreshLibrary();
    }

    private void CleanupAllSymlinks(string symlinkDir)
    {
        if (!Directory.Exists(symlinkDir))
        {
            return;
        }

        foreach (var link in _symlinkManager.ListSymlinks(symlinkDir))
        {
            try
            {
                _symlinkManager.RemoveSymlink(link.Path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove symlink {Path}", link.Path);
            }
        }
    }

    private string? ResolvePath(LeavingSoonItem item)
    {
        if (!Guid.TryParse(item.MediaServerId, out var guid))
        {
            return null;
        }

        var baseItem = _libraryManager.GetItemById(guid);
        return string.IsNullOrWhiteSpace(baseItem?.Path) ? null : baseItem.Path;
    }
}
