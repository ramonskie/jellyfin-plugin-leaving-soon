using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeavingSoon.Configuration;
using Jellyfin.Plugin.LeavingSoon.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LeavingSoon.Api;

/// <summary>
/// API controller for the Leaving Soon plugin (status / debug endpoints).
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("api/leaving-soon")]
[Produces("application/json")]
public class LeavingSoonController : ControllerBase
{
    private readonly ILogger<LeavingSoonController> _logger;
    private readonly SyncService _syncService;

    /// <summary>
    /// Initializes a new instance of the <see cref="LeavingSoonController"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="syncService">The sync service.</param>
    public LeavingSoonController(ILogger<LeavingSoonController> logger, SyncService syncService)
    {
        _logger = logger;
        _syncService = syncService;
    }

    /// <summary>
    /// Gets the plugin status and configuration summary.
    /// </summary>
    /// <returns>Status response.</returns>
    [HttpGet("status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StatusResponse> GetStatus()
    {
        var config = Plugin.Instance?.Configuration;
        return Ok(new StatusResponse
        {
            Status = "ok",
            Version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
            ProviderCount = config?.Providers.Count(p => p.Enabled) ?? 0,
            BasePath = config?.BasePath ?? string.Empty,
            MoviesLibraryName = config?.MoviesLibraryName ?? string.Empty,
            TvLibraryName = config?.TvLibraryName ?? string.Empty,
            HideWhenEmpty = config?.HideWhenEmpty ?? true,
            SyncIntervalMinutes = config?.SyncIntervalMinutes ?? 15,
        });
    }

    /// <summary>
    /// Triggers an immediate sync (for debugging / integration tests).
    /// </summary>
    /// <returns>Accepted response.</returns>
    [HttpPost("sync")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult TriggerSync()
    {
        _logger.LogInformation("Manual sync triggered via API");

        // Fire-and-forget with a progress reporter that does nothing.
        _ = Task.Run(() => _syncService.ExecuteAsync(new System.Progress<double>(), CancellationToken.None));
        return Accepted();
    }
}

/// <summary>
/// Status response model.
/// </summary>
public class StatusResponse
{
    /// <summary>
    /// Gets or sets the status string.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the plugin version.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of enabled providers.
    /// </summary>
    public int ProviderCount { get; set; }

    /// <summary>
    /// Gets or sets the configured base path.
    /// </summary>
    public string BasePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the movies library name.
    /// </summary>
    public string MoviesLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TV library name.
    /// </summary>
    public string TvLibraryName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether empty libraries are hidden.
    /// </summary>
    public bool HideWhenEmpty { get; set; }

    /// <summary>
    /// Gets or sets the sync interval in minutes.
    /// </summary>
    public int SyncIntervalMinutes { get; set; }
}
