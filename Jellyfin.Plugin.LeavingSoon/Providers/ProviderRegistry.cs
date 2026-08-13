using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.LeavingSoon.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LeavingSoon.Providers;

/// <summary>
/// Builds and owns the configured providers.
/// </summary>
public class ProviderRegistry
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ProviderRegistry> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProviderRegistry"/> class.
    /// </summary>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="logger">The logger.</param>
    public ProviderRegistry(ILoggerFactory loggerFactory, ILogger<ProviderRegistry> logger)
    {
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Builds the enabled providers from the plugin configuration.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <returns>The list of enabled providers.</returns>
    public IReadOnlyList<ILeavingSoonProvider> BuildProviders(PluginConfiguration config)
    {
        var providers = new List<ILeavingSoonProvider>();

        foreach (var providerConfig in config.Providers)
        {
            if (!providerConfig.Enabled)
            {
                continue;
            }

            try
            {
                switch (providerConfig.Type.ToUpperInvariant())
                {
                    case "MAINTAINERR":
                        providers.Add(new MaintainerrProvider(
                            providerConfig,
                            _loggerFactory.CreateLogger<MaintainerrProvider>()));
                        break;
                    case "OXICLEANARR":
                        providers.Add(new OxiCleanarrProvider(
                            providerConfig,
                            _loggerFactory.CreateLogger<OxiCleanarrProvider>()));
                        break;
                    default:
                        _logger.LogWarning(
                            "Ignoring provider '{Name}' with unknown type '{Type}'",
                            providerConfig.Name,
                            providerConfig.Type);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to build provider '{Name}'", providerConfig.Name);
            }
        }

        return providers;
    }

    /// <summary>
    /// Fetches items from all enabled providers and dedupes by media server id.
    /// A failing provider contributes nothing and is logged; it never aborts the sync.
    /// </summary>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deduped leaving-soon items and the number of provider failures.</returns>
    public async Task<(IReadOnlyList<LeavingSoonItem> Items, int FailureCount)> CollectItemsAsync(
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var providers = BuildProviders(config);
        if (providers.Count == 0)
        {
            return ([], 0);
        }

        var byId = new Dictionary<string, LeavingSoonItem>(StringComparer.OrdinalIgnoreCase);
        var failures = 0;

        foreach (var provider in providers)
        {
            try
            {
                var items = await provider.GetLeavingSoonItemsAsync(cancellationToken);
                foreach (var item in items)
                {
                    // First provider wins on a tie (config order = priority).
                    byId.TryAdd(item.MediaServerId, item);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures++;
                _logger.LogWarning(
                    ex,
                    "Provider '{Provider}' failed during poll",
                    provider.Name);
            }
        }

        return (byId.Values.ToList(), failures);
    }
}
