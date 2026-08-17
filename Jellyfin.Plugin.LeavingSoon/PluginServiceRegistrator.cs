using Jellyfin.Plugin.LeavingSoon.Providers;
using Jellyfin.Plugin.LeavingSoon.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LeavingSoon;

/// <summary>
/// Register services for dependency injection.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Hand the running app host to the plugin so OnUninstalling can resolve the
        // cleanup services after the DI container is built.
        Plugin.ApplicationHost = applicationHost;

        serviceCollection.AddSingleton<SymlinkManager>();
        serviceCollection.AddSingleton<VirtualFolderManager>();
        serviceCollection.AddSingleton<ItemImageSync>();
        serviceCollection.AddSingleton<ProviderRegistry>();
        serviceCollection.AddSingleton<SyncService>();
    }
}
