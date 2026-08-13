using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeavingSoon.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LeavingSoon.Providers;

/// <summary>
/// Pulls leaving-soon media from OxiCleanarr's GET /api/media/leaving-soon endpoint.
/// OxiCleanarr typically runs with admin.disable_auth=true for machine clients; the
/// plugin also supports an API key sent as a Bearer token.
/// </summary>
public sealed class OxiCleanarrProvider : ILeavingSoonProvider, IDisposable
{
    private readonly ProviderConfig _config;
    private readonly ILogger<OxiCleanarrProvider> _logger;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="OxiCleanarrProvider"/> class.
    /// </summary>
    /// <param name="config">The provider configuration.</param>
    /// <param name="logger">The logger.</param>
    public OxiCleanarrProvider(ProviderConfig config, ILogger<OxiCleanarrProvider> logger)
    {
        _config = config;
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }
    }

    /// <inheritdoc />
    public string Type => "oxicleanarr";

    /// <inheritdoc />
    public string Name => string.IsNullOrWhiteSpace(_config.Name) ? "oxicleanarr" : _config.Name;

    /// <inheritdoc />
    public async Task<IReadOnlyList<LeavingSoonItem>> GetLeavingSoonItemsAsync(CancellationToken cancellationToken)
    {
        var url = new Uri($"{_config.Url.TrimEnd('/')}/api/media/leaving-soon");
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<OxiCleanarrLeavingSoonResponse>(
            stream,
            JsonDefaults.Options,
            cancellationToken);

        if (payload?.Items == null)
        {
            return [];
        }

        var items = payload.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.MediaServerId))
            .Select(i => new LeavingSoonItem
            {
                MediaServerId = i.MediaServerId,
                Type = i.Type == "show" ? "show" : "movie",
                Title = i.Title,
                DeletionDate = i.DeletionDate,
                SourcePath = i.SourcePath,
            })
            .ToList();

        _logger.LogDebug(
            "OxiCleanarr provider '{Provider}' returned {Count} leaving-soon items",
            Name,
            items.Count);
        return items;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// OxiCleanarr leaving-soon API response envelope.
/// </summary>
public class OxiCleanarrLeavingSoonResponse
{
    /// <summary>
    /// Gets or sets the contract version.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>
    /// Gets or sets the leaving-soon items.
    /// </summary>
    [JsonPropertyName("items")]
    public List<OxiCleanarrLeavingSoonItem> Items { get; set; } = [];
}

/// <summary>
/// A leaving-soon item as returned by OxiCleanarr.
/// </summary>
public class OxiCleanarrLeavingSoonItem
{
    /// <summary>
    /// Gets or sets the Jellyfin item id.
    /// </summary>
    [JsonPropertyName("mediaServerId")]
    public string MediaServerId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the media type ("movie" or "show").
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "movie";

    /// <summary>
    /// Gets or sets the item title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the scheduled deletion date.
    /// </summary>
    [JsonPropertyName("deletionDate")]
    public DateTime? DeletionDate { get; set; }

    /// <summary>
    /// Gets or sets the provider-supplied source path (optional).
    /// </summary>
    [JsonPropertyName("sourcePath")]
    public string? SourcePath { get; set; }
}
