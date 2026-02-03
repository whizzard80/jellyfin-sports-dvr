using Jellyfin.Plugin.SportsDVR.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SportsDVR;

/// <summary>
/// Registers plugin services for dependency injection.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        // Core services
        services.AddSingleton<EpgParser>();
        services.AddSingleton<PatternMatcher>();
        services.AddSingleton<SportsScorer>();
        services.AddSingleton<AliasService>();
        services.AddSingleton<SubscriptionManager>();
        services.AddSingleton<SmartScheduler>();

        // Background service for EPG scanning and Jellyfin DVR integration
        services.AddHostedService<RecordingScheduler>();
    }
}
