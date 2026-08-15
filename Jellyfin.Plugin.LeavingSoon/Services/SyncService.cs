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

    /// <summary>
    /// Builds a diagnostic snapshot of the current sync inputs: the configured
    /// providers and whether each collected leaving-soon item resolves to a Jellyfin
    /// path. Backs the debug endpoint so a "sync reconciled to nothing" situation can
    /// be traced without server log access.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The diagnostic snapshot.</returns>
    public async Task<SyncDiagnostics> DiagnoseAsync(CancellationToken cancellationToken)
    {
        // Take the sync lock so the snapshot is consistent and a debug hit can't
        // double-poll the providers while a scheduled sync is mid-run. If a sync is
        // running, the debug response simply waits for it to finish.
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = new SyncDiagnostics();
            var config = Plugin.Instance?.Configuration;
            if (config == null)
            {
                return result;
            }

            result.ConfiguredProviders = config.Providers
                .Select(p => new ProviderRecord(p.Name, p.Type, p.Enabled, p.Url))
                .ToList();

            var (items, failureCount) = await _providerRegistry
                .CollectItemsAsync(config, cancellationToken)
                .ConfigureAwait(false);

            result.ProviderFailures = failureCount;
            result.ConsecutiveProviderFailures = _consecutiveProviderFailures;

            foreach (var item in items)
            {
                var path = ResolvePath(item);
                result.Items.Add(new ItemRecord(item.MediaServerId, item.Title ?? string.Empty, item.Type, path));
                if (!string.IsNullOrWhiteSpace(path))
                {
                    result.ResolvedCount++;
                }
            }

            return result;
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

        // Hide-when-empty: disable the library instead of deleting it. LibraryOptions.Enabled
        // (CollectionFolder.IsVisible) is the server-native "enable this library" toggle; disabling
        // hides the library from all user views while keeping its metadata rows and symlinks
        // intact, so re-enabling on the next sync with items is instant with zero rescan.
        if (items.Count == 0)
        {
            if (!config.HideWhenEmpty)
            {
                _logger.LogDebug("Library {Name} is empty but hide_when_empty is false, leaving it", libraryName);
                return;
            }

            if (_virtualFolderManager.GetVirtualFolder(libraryName) == null)
            {
                _logger.LogDebug("Library {Name} does not exist yet, nothing to disable", libraryName);
                return;
            }

            _logger.LogInformation("Library {Name} is empty - disabling (hide_when_empty)", libraryName);
            await _virtualFolderManager.SetLibraryEnabledAsync(libraryName, false).ConfigureAwait(false);
            return;
        }

        // Ensure the host directory and virtual folder exist. AddVirtualFolder already
        // makes the new library show up in user views, so no separate view refresh is
        // needed here.
        _symlinkManager.EnsureDirectoryExists(symlinkDir);
        var (_, needsInitialScan) = await _virtualFolderManager
            .EnsureVirtualFolderAsync(libraryName, collectionType, symlinkDir)
            .ConfigureAwait(false);

        // A library disabled while empty must be brought back before the content refresh,
        // or it stays hidden even after the scan.
        await _virtualFolderManager.SetLibraryEnabledAsync(libraryName, true).ConfigureAwait(false);

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

        // Refresh so newly-created symlinks show up. A brand-new library (or a freshly
        // added path) needs a global scan: the physical folder at the symlink path only
        // gets materialized by a scan, and a scoped path refresh has nothing to resolve
        // to before that. Once the library exists, a scoped refresh of the symlink
        // directory is enough to pick up added/removed symlinks.
        if (needsInitialScan)
        {
            _virtualFolderManager.RefreshLibrary();
        }
        else
        {
            _virtualFolderManager.QuickRefreshPath(symlinkDir);
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

/// <summary>
/// Diagnostic snapshot of a sync run, exposed by the debug endpoint.
/// </summary>
public class SyncDiagnostics
{
    /// <summary>
    /// Gets or sets the configured providers (enabled or not).
    /// </summary>
    public List<ProviderRecord> ConfiguredProviders { get; set; } = [];

    /// <summary>
    /// Gets or sets the number of providers that failed during the diagnostic poll.
    /// </summary>
    public int ProviderFailures { get; set; }

    /// <summary>
    /// Gets or sets the running consecutive-failure counter used by the provider-outage guard.
    /// </summary>
    public int ConsecutiveProviderFailures { get; set; }

    /// <summary>
    /// Gets or sets the number of collected items that resolved to a Jellyfin path.
    /// </summary>
    public int ResolvedCount { get; set; }

    /// <summary>
    /// Gets or sets the collected leaving-soon items with their resolution status.
    /// </summary>
    public List<ItemRecord> Items { get; set; } = [];
}

/// <summary>
/// A configured provider and its enabled flag.
/// </summary>
public class ProviderRecord
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderRecord"/> class.
    /// </summary>
    /// <param name="name">The provider display name.</param>
    /// <param name="type">The provider type.</param>
    /// <param name="enabled">Whether the provider is enabled.</param>
    /// <param name="url">The provider base url.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1054:Uri parameters should not be strings",
        Justification = "Mirrors ProviderConfig.Url; kept as a string so the debug endpoint can surface unresolved settings.")]
    public ProviderRecord(string name, string type, bool enabled, string url)
    {
        Name = name;
        Type = type;
        Enabled = enabled;
        Url = url;
    }

    /// <summary>
    /// Gets the provider display name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the provider type ("oxicleanarr" or "maintainerr").
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// Gets a value indicating whether the provider is enabled.
    /// </summary>
    public bool Enabled { get; }

    /// <summary>
    /// Gets the provider base url.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1056:Uri properties should not be strings",
        Justification = "Mirrors ProviderConfig.Url; exposed so the debug endpoint can surface unresolved settings.")]
    public string Url { get; }
}

/// <summary>
/// A collected leaving-soon item and whether it resolved to a Jellyfin path.
/// </summary>
public class ItemRecord
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ItemRecord"/> class.
    /// </summary>
    /// <param name="mediaServerId">The Jellyfin item id reported by the provider.</param>
    /// <param name="title">The item title.</param>
    /// <param name="type">The media type ("movie" or "show").</param>
    /// <param name="path">The resolved Jellyfin path, or null when unresolvable.</param>
    public ItemRecord(string mediaServerId, string title, string type, string? path)
    {
        MediaServerId = mediaServerId;
        Title = title;
        Type = type;
        Path = path;
    }

    /// <summary>
    /// Gets the Jellyfin item id reported by the provider.
    /// </summary>
    public string MediaServerId { get; }

    /// <summary>
    /// Gets the item title.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the media type ("movie" or "show").
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// Gets the resolved Jellyfin path, or null when unresolvable.
    /// </summary>
    public string? Path { get; }

    /// <summary>
    /// Gets a value indicating whether the item resolved to a Jellyfin path.
    /// </summary>
    public bool Resolved => !string.IsNullOrWhiteSpace(Path);
}
