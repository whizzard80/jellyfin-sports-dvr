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

    /// <summary>
    /// Gets or sets the Jellyfin library name for sports recordings.
    /// Default: "Sports DVR"
    /// </summary>
    public string SportsLibraryName { get; set; } = "Sports DVR";

    /// <summary>
    /// Gets or sets the path where DVR/post-processor dumps all recordings (SOURCE).
    /// This is where the organize function looks for sports games to move.
    /// Default: /mnt/movies/hyperdata/dvr/
    /// </summary>
    public string DvrRecordingsPath { get; set; } = "/mnt/movies/hyperdata/dvr/";

    /// <summary>
    /// Gets or sets the path where sports recordings should be moved to (DESTINATION).
    /// Sports games found in DvrRecordingsPath will be moved here.
    /// Default: /mnt/movies/hyperdata/dvr/sports-dvr/
    /// </summary>
    public string SportsRecordingsPath { get; set; } = "/mnt/movies/hyperdata/dvr/sports-dvr/";

    /// <summary>
    /// Gets or sets the folder organization strategy for sports recordings.
    /// Options: "Subscription" (by team/league/event), "Date" (by date), "League" (by league then date), "None" (flat structure)
    /// Default: "League"
    /// </summary>
    public string FolderOrganization { get; set; } = "League";

    /// <summary>
    /// Gets or sets whether to automatically organize recordings into sports folder.
    /// If true, plugin will move matching recordings to SportsRecordingsPath.
    /// </summary>
    public bool AutoOrganizeRecordings { get; set; } = false;
}
