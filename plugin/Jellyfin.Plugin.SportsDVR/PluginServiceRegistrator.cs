using Jellyfin.Plugin.SportsDVR.Services;
using Jellyfin.Plugin.SportsDVR.Tasks;
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
        services.AddSingleton<GuideCachePurgeService>();

        // Recording scheduler (singleton, called by the scheduled task)
        services.AddSingleton<RecordingScheduler>();

        // Jellyfin scheduled task - appears in Dashboard → Scheduled Tasks
        services.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, EpgScanTask>();
    }
}
