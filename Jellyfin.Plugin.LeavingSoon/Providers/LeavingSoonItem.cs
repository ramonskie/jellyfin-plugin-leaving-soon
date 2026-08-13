using System;

namespace Jellyfin.Plugin.LeavingSoon.Providers;

/// <summary>
/// The normalized leaving-soon item returned by every provider.
/// </summary>
public class LeavingSoonItem
{
    /// <summary>
    /// Gets or sets the Jellyfin item id (a Jellyfin GUID), NOT a TMDB/arr id.
    /// </summary>
    public string MediaServerId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the media type: "movie" or "show".
    /// </summary>
    public string Type { get; set; } = "movie";

    /// <summary>
    /// Gets or sets the item title (informational only).
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the scheduled deletion date, if known.
    /// </summary>
    public DateTime? DeletionDate { get; set; }

    /// <summary>
    /// Gets or sets an optional provider-supplied source path. The plugin prefers
    /// resolving the path from Jellyfin itself and only falls back to this.
    /// </summary>
    public string? SourcePath { get; set; }
}
