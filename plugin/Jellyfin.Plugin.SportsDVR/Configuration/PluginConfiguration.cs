using System.Collections.Generic;
using Jellyfin.Plugin.SportsDVR.Models;
using Jellyfin.Plugin.SportsDVR.Services;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SportsDVR.Configuration;

/// <summary>
/// Plugin configuration for Sports DVR.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        MaxConcurrentRecordings = 2;
        DefaultPriority = 50;
        Subscriptions = new List<Subscription>();
        CustomAliases = new List<CustomTeamAlias>();
        EnableAutoScheduling = true;
        EnableAliasMatching = true;
        ScanIntervalMinutes = 5;
    }

    /// <summary>
    /// Gets or sets the maximum number of concurrent recordings.
    /// Limited by tuner/IPTV connection count.
    /// </summary>
    public int MaxConcurrentRecordings { get; set; }

    /// <summary>
    /// Gets or sets the default priority for new subscriptions (1-100).
    /// </summary>
    public int DefaultPriority { get; set; }

    /// <summary>
    /// Gets or sets whether auto-scheduling from EPG is enabled.
    /// </summary>
    public bool EnableAutoScheduling { get; set; }

    /// <summary>
    /// Gets or sets how often to scan EPG for matching programs (minutes).
    /// </summary>
    public int ScanIntervalMinutes { get; set; }

    /// <summary>
    /// Gets or sets the list of subscriptions.
    /// </summary>
    public List<Subscription> Subscriptions { get; set; }

    /// <summary>
    /// Gets or sets custom team aliases defined by the user.
    /// </summary>
    public List<CustomTeamAlias> CustomAliases { get; set; }

    /// <summary>
    /// Gets or sets whether to use alias matching for team names.
    /// When true, "Man City" will match a "Manchester City" subscription.
    /// </summary>
    public bool EnableAliasMatching { get; set; }
}
