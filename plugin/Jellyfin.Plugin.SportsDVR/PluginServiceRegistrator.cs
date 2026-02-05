using Jellyfin.Plugin.SportsDVR.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        services.AddSingleton<SportsLibraryService>();

        // Register RecordingScheduler as singleton so it can be injected
        services.AddSingleton<RecordingScheduler>();
        
        // Also register it as a hosted service (using the same singleton instance)
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RecordingScheduler>());
    }
}
