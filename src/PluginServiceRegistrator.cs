using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jellyflix;

/// <summary>
/// Jellyfin discovers this via reflection on plugin load and calls
/// RegisterServices. Use it to wire anything the plugin needs into
/// the host's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        // Runs Bootstrapper.StartAsync when the server finishes starting up.
        services.AddHostedService<Services.Bootstrapper>();

        // Cache the health status so /Jellyflix/Health is cheap to poll.
        services.AddSingleton<Services.HealthState>();
    }
}
