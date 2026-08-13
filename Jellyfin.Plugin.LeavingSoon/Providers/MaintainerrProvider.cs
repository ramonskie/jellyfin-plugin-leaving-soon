using System;
using System.Collections.Generic;
using System.Globalization;
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
/// Pulls leaving-soon media from Maintainerr's GET /api/collections/leaving-soon endpoint.
/// </summary>
public sealed class MaintainerrProvider : ILeavingSoonProvider, IDisposable
{
    private readonly ProviderConfig _config;
    private readonly ILogger<MaintainerrProvider> _logger;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="MaintainerrProvider"/> class.
    /// </summary>
    /// <param name="config">The provider configuration.</param>
    /// <param name="logger">The logger.</param>
    public MaintainerrProvider(ProviderConfig config, ILogger<MaintainerrProvider> logger)
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
    public string Type => "maintainerr";

    /// <inheritdoc />
    public string Name => string.IsNullOrWhiteSpace(_config.Name) ? "maintainerr" : _config.Name;

    /// <inheritdoc />
    public async Task<IReadOnlyList<LeavingSoonItem>> GetLeavingSoonItemsAsync(CancellationToken cancellationToken)
    {
        var includeCollections = _config.IncludeCollections
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var url = new Uri($"{_config.Url.TrimEnd('/')}/api/collections/leaving-soon");
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<MaintainerrLeavingSoonResponse>(
            stream,
            JsonDefaults.Options,
            cancellationToken);

        if (payload?.Collections == null)
        {
            return [];
        }

        var items = new List<LeavingSoonItem>();
        foreach (var collection in payload.Collections)
        {
            if (includeCollections.Count > 0 &&
                !includeCollections.Contains(collection.Id.ToString(CultureInfo.InvariantCulture)))
            {
                continue;
            }

            foreach (var media in collection.Media)
            {
                if (string.IsNullOrWhiteSpace(media.MediaServerId))
                {
                    continue;
                }

                items.Add(new LeavingSoonItem
                {
                    MediaServerId = media.MediaServerId,
                    Type = collection.Type == "show" ? "show" : "movie",
                    Title = collection.Title,
                    DeletionDate = media.DeletionDate,
                });
            }
        }

        _logger.LogDebug(
            "Maintainerr provider '{Provider}' returned {Count} leaving-soon items",
            Name,
            items.Count);
        return items;
    }

    /// <inheritdoc />
    public async Task<ProviderTestResult> TestConnectionAsync(CancellationToken cancellationToken)
    {
        var url = new Uri($"{_config.Url.TrimEnd('/')}/api/collections/leaving-soon");
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var payload = JsonSerializer.Deserialize<MaintainerrLeavingSoonResponse>(content, JsonDefaults.Options);
            var count = payload?.Collections?.Sum(c => c.Media.Count) ?? 0;
            return new ProviderTestResult(true, $"Connected. {count} scheduled-deletion media item(s) found.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Test connection to Maintainerr '{Provider}' failed", Name);
            return new ProviderTestResult(false, ex.Message);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Maintainerr leaving-soon API response envelope.
/// </summary>
public class MaintainerrLeavingSoonResponse
{
    /// <summary>
    /// Gets or sets the collections.
    /// </summary>
    [JsonPropertyName("collections")]
    public List<MaintainerrLeavingSoonCollectionEntry> Collections { get; set; } = [];
}

/// <summary>
/// A Maintainerr collection scheduled for deletion.
/// </summary>
public class MaintainerrLeavingSoonCollectionEntry
{
    /// <summary>
    /// Gets or sets the collection id.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the collection title.
    /// </summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the media type ("movie" or "show").
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "movie";

    /// <summary>
    /// Gets or sets the member media.
    /// </summary>
    [JsonPropertyName("media")]
    public List<MaintainerrLeavingSoonMedia> Media { get; set; } = [];
}

/// <summary>
/// A media item in a Maintainerr leaving-soon collection.
/// </summary>
public class MaintainerrLeavingSoonMedia
{
    /// <summary>
    /// Gets or sets the Jellyfin item id.
    /// </summary>
    [JsonPropertyName("mediaServerId")]
    public string MediaServerId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the scheduled deletion date.
    /// </summary>
    [JsonPropertyName("deletionDate")]
    public DateTime? DeletionDate { get; set; }
}
