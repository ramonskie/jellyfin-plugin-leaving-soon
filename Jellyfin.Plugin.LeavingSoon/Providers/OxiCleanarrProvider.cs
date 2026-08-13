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
/// OxiCleanarr accepts the static admin.api_key as a Bearer token on every protected
/// endpoint (or can run with admin.disable_auth=true); set ApiKey to that key when
/// auth is enabled.
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
            .Select(i =>
            {
                if (!i.Type.Equals("movie", StringComparison.OrdinalIgnoreCase) &&
                    !i.Type.Equals("show", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug(
                        "Provider '{Provider}' returned unknown media type '{Type}' for '{Title}'; treating as movie",
                        Name,
                        i.Type,
                        i.Title);
                }

                return new LeavingSoonItem
                {
                    MediaServerId = i.MediaServerId,
                    Type = NormalizeType(i.Type),
                    Title = i.Title,
                    DeletionDate = i.DeletionDate,
                    SourcePath = i.SourcePath,
                };
            })
            .ToList();

        _logger.LogDebug(
            "OxiCleanarr provider '{Provider}' returned {Count} leaving-soon items",
            Name,
            items.Count);
        return items;
    }

    /// <inheritdoc />
    public async Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var url = new Uri($"{_config.Url.TrimEnd('/')}/api/media/leaving-soon");
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var payload = JsonSerializer.Deserialize<OxiCleanarrLeavingSoonResponse>(content, JsonDefaults.Options);
            var count = payload?.Items?.Count ?? 0;
            return new ProviderTestResult(true, $"Connected. {count} leaving-soon item(s) found.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Test connection to OxiCleanarr '{Provider}' failed", Name);
            return new ProviderTestResult(false, ex.Message);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Normalizes a provider media type to the plugin's "movie"/"show" values.
    /// Anything outside the contract is treated as a movie so an unknown type
    /// never leaks into the library partitioning.
    /// </summary>
    private static string NormalizeType(string? type)
    {
        if (string.Equals(type, "show", StringComparison.OrdinalIgnoreCase))
        {
            return "show";
        }

        return "movie";
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
