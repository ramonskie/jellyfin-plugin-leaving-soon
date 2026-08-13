using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeavingSoon.Configuration;
using Jellyfin.Plugin.LeavingSoon.Providers;
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
    private readonly ProviderRegistry _providerRegistry;

    /// <summary>
    /// Initializes a new instance of the <see cref="LeavingSoonController"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="syncService">The sync service.</param>
    /// <param name="providerRegistry">The provider registry.</param>
    public LeavingSoonController(
        ILogger<LeavingSoonController> logger,
        SyncService syncService,
        ProviderRegistry providerRegistry)
    {
        _logger = logger;
        _syncService = syncService;
        _providerRegistry = providerRegistry;
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

    /// <summary>
    /// Tests connectivity to a provider using the supplied (possibly unsaved) settings.
    /// </summary>
    /// <param name="request">The provider settings to test.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The connection test result.</returns>
    [HttpPost("test-connection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TestConnectionResponse>> TestConnection(
        [FromBody] TestConnectionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Url))
        {
            return BadRequest(new TestConnectionResponse
            {
                Success = false,
                Message = "Type and Url are required.",
            });
        }

        var providerConfig = new ProviderConfig
        {
            Type = request.Type,
            Name = request.Name,
            Url = request.Url,
            ApiKey = request.ApiKey,
            IncludeCollections = request.IncludeCollections,
        };

        var provider = _providerRegistry.BuildProvider(providerConfig);
        if (provider == null)
        {
            return BadRequest(new TestConnectionResponse
            {
                Success = false,
                Message = $"Unknown provider type '{request.Type}'.",
            });
        }

        using (provider as IDisposable)
        {
            var result = await provider.TestConnectionAsync(cancellationToken);
            _logger.LogInformation(
                "Connection test for provider '{Provider}' ({Type}): {Outcome}",
                provider.Name,
                provider.Type,
                result.Success ? "success" : "failed");
            return Ok(new TestConnectionResponse
            {
                Success = result.Success,
                Message = result.Message,
            });
        }
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

/// <summary>
/// Request model for testing a provider connection.
/// </summary>
public class TestConnectionRequest
{
    /// <summary>
    /// Gets or sets the provider kind ("maintainerr" or "oxicleanarr").
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the provider base URL.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1056:Uri properties should not be strings",
        Justification = "Mirrors ProviderConfig.Url; kept as a string so the config page can send unsaved values.")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional API key.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional comma-separated collection ids.
    /// </summary>
    public string IncludeCollections { get; set; } = string.Empty;
}

/// <summary>
/// Response model for a provider connection test.
/// </summary>
public class TestConnectionResponse
{
    /// <summary>
    /// Gets or sets a value indicating whether the connection test succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Gets or sets a human-readable description of the outcome.
    /// </summary>
    public string Message { get; set; } = string.Empty;
}
