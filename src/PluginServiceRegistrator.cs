using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Jellyflix;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddHostedService<Services.Bootstrapper>();
        services.AddSingleton<Services.HealthState>();

        // Singleton: owns the per-user rail cache, so it must live for the host lifetime.
        services.AddSingleton<Services.RecommendationEngine>();
    }
}
