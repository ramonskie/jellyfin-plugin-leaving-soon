using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.LeavingSoon.Configuration;

/// <summary>
/// Configuration for the Leaving Soon plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        BasePath = "/config/leaving-soon";
        MoviesLibraryName = "Movies - Leaving Soon";
        TvLibraryName = "Shows - Leaving Soon";
        HideWhenEmpty = true;
        SyncIntervalMinutes = 15;
        Providers = [];
        ForceEmptyAfterFailureCount = 3;
    }

    /// <summary>
    /// Gets or sets the host directory under which the movies and tv subdirectories live.
    /// </summary>
    public string BasePath { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin library name for movies.
    /// </summary>
    public string MoviesLibraryName { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin library name for TV shows.
    /// </summary>
    public string TvLibraryName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether empty libraries are deleted when no items are scheduled.
    /// </summary>
    public bool HideWhenEmpty { get; set; }

    /// <summary>
    /// Gets or sets the sync interval in minutes.
    /// </summary>
    public int SyncIntervalMinutes { get; set; }

    /// <summary>
    /// Gets or sets how many consecutive provider failures are tolerated before an empty
    /// result is treated as authoritative (and empty libraries deleted). Prevents a
    /// temporarily-down provider from wiping the leaving-soon libraries.
    /// </summary>
    public int ForceEmptyAfterFailureCount { get; set; }

    /// <summary>
    /// Gets or sets the configured providers.
    /// </summary>
    public List<ProviderConfig> Providers { get; set; }
}

/// <summary>
/// Configuration for a single provider.
/// </summary>
public class ProviderConfig
{
    /// <summary>
    /// Gets or sets the provider kind: "maintainerr" or "oxicleanarr".
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a display name for this provider instance.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether this provider is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the base URL of the provider (no trailing slash).
    /// Kept as a string so it serializes cleanly in the plugin XML config.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1056:Uri properties should not be strings",
        Justification = "XML plugin configuration serializes better as a string.")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional API key sent as a Bearer token.
    /// Maintainerr has no enforced auth today; OxiCleanarr may run with auth disabled.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional comma-separated list of Maintainerr collection ids to include.
    /// Empty means all scheduled-deletion collections.
    /// </summary>
    public string IncludeCollections { get; set; } = string.Empty;
}
