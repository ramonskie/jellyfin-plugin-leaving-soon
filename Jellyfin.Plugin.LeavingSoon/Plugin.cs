using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.LeavingSoon.Configuration;
using Jellyfin.Plugin.LeavingSoon.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LeavingSoon;

/// <summary>
/// The main plugin class.
/// </summary>
#pragma warning disable SA1201 // Elements should appear in correct order - Instance property pattern
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets or sets the server application host, wired up by <see cref="PluginServiceRegistrator"/>.
    /// Used only to resolve services during uninstall cleanup, after the DI container is built.
    /// </summary>
    public static IServerApplicationHost? ApplicationHost { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override void OnUninstalling()
    {
        try
        {
            CleanUpLibraries();
        }
        catch (Exception ex)
        {
            // Swallow: uninstall must not fail because housekeeping errored.
            Logger?.LogError(ex, "Leaving Soon uninstall cleanup failed");
        }
    }

    private void CleanUpLibraries()
    {
        var config = Configuration;
        var services = ApplicationHost?.ServiceProvider;
        if (services == null)
        {
            return;
        }

        var symlinkManager = services.GetService<SymlinkManager>();
        var virtualFolderManager = services.GetService<VirtualFolderManager>();

        // Remove all symlinks the plugin created under the base path, then drop each
        // subdirectory only when it is now empty. Never recursive-delete: BasePath could
        // point at a location that already contained real content (or a real library),
        // and that data must survive uninstall. Each subdirectory is isolated so a
        // failure cleaning one (e.g. an unreadable path) cannot strand the other.
        if (string.IsNullOrWhiteSpace(config.BasePath))
        {
            // Mirrors the ownership guard's fail-closed behavior on a blank base path:
            // Path.Combine("", "movies") would resolve relative to the server CWD and
            // could touch unrelated symlinks, so skip the filesystem pass entirely.
            Logger?.LogWarning("Leaving Soon base path is blank; skipping symlink cleanup during uninstall");
        }
        else
        {
            foreach (var subDir in new[] { SyncService.MoviesSubDir, SyncService.TvSubDir })
            {
                try
                {
                    var dir = Path.Combine(config.BasePath, subDir);
                    symlinkManager?.RemoveAllSymlinks(dir);
                    if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                    {
                        Directory.Delete(dir);
                    }
                }
                catch (Exception ex)
                {
                    // A failure here (e.g. unreadable base path) must not abort cleanup of the
                    // other subdirectory nor the library removal below, so swallow and continue.
                    Logger?.LogWarning(ex, "Leaving Soon symlink cleanup failed for {SubDir} during uninstall", subDir);
                }
            }
        }

        // Remove the virtual folders the plugin created. Safe to call when a library was
        // never created or was renamed by the admin: it is a no-op when the name is absent.
        //
        // DeleteVirtualFolderAsync disables the library before removing it so it drops out
        // of user views instantly (and stays hidden even if removal fails partway), and it
        // guards the delete with the owned-path (BasePath + sub-directory): removal only
        // proceeds when every location lives under that path, so a library whose configured
        // name collides with (or whose symlink path was merged into) a real admin-created
        // library is left untouched — uninstall never disables or deletes real libraries.
        // Each library is isolated so one library failing to remove cannot strand the other.
        foreach (var library in new[]
        {
            new { Name = config.MoviesLibraryName, SubDir = SyncService.MoviesSubDir },
            new { Name = config.TvLibraryName, SubDir = SyncService.TvSubDir },
        })
        {
            try
            {
                virtualFolderManager?.DeleteVirtualFolderAsync(
                    library.Name,
                    Path.Combine(config.BasePath, library.SubDir)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Leaving Soon virtual folder cleanup failed for {Name} during uninstall", library.Name);
            }
        }
    }

    /// <summary>
    /// Gets a logger for the plugin, resolving it lazily via the application host.
    /// </summary>
    private static ILogger<Plugin>? Logger => GetOrCreateLogger();

    private static ILogger<Plugin>? GetOrCreateLogger()
    {
        var provider = ApplicationHost?.ServiceProvider;
        return provider?.GetService<ILoggerFactory>()?.CreateLogger<Plugin>();
    }

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("b31d2e5a-8f4e-4c6a-b7a3-2d4e5f6a7b8c");

    /// <inheritdoc />
    public override string Name => "Leaving Soon";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
            }
        ];
    }
}
