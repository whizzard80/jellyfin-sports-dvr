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

    /// <summary>
    /// Gets or sets the primary region for time-based filtering.
    /// This affects when live games are expected to air.
    /// Options: "USA", "Europe", "Asia", "Australia", "All" (no time filtering)
    /// Default: "USA"
    /// </summary>
    public string PrimaryRegion { get; set; } = "USA";
}

/// <summary>
/// Defines time windows for live sports by region.
/// </summary>
public static class RegionTimeWindows
{
    /// <summary>
    /// Gets the typical live sports time window for a region.
    /// Returns (startHour, endHour) in UTC.
    /// </summary>
    public static (int StartHour, int EndHour, int EuroFootballStart, int EuroFootballEnd) GetTimeWindow(string region)
    {
        return region?.ToUpperInvariant() switch
        {
            // USA: 12 PM - 12 AM EST = 17:00 - 05:00 UTC
            "USA" => (17, 5, 11, 21),  // Euro football: 6 AM - 4 PM EST = 11:00 - 21:00 UTC
            
            // Europe: 2 PM - 11 PM CET = 13:00 - 22:00 UTC (local prime time)
            "EUROPE" => (13, 22, 13, 22),  // Euro football same window
            
            // Asia: 6 PM - 12 AM JST/KST = 09:00 - 15:00 UTC
            "ASIA" => (9, 15, 6, 12),  // Euro football: early morning local
            
            // Australia: 5 PM - 12 AM AEST = 07:00 - 14:00 UTC
            "AUSTRALIA" => (7, 14, 20, 6),  // Euro football: late night/early morning local
            
            // All: No time filtering - accept any time
            "ALL" or _ => (-1, -1, -1, -1)
        };
    }

    /// <summary>
    /// Checks if a given UTC hour falls within the live sports window for a region.
    /// </summary>
    public static bool IsWithinLiveWindow(string region, int utcHour, bool isEuropeanFootball = false)
    {
        var (start, end, euroStart, euroEnd) = GetTimeWindow(region);
        
        // "All" region - no time filtering
        if (start == -1) return true;
        
        int windowStart = isEuropeanFootball ? euroStart : start;
        int windowEnd = isEuropeanFootball ? euroEnd : end;
        
        // Handle windows that cross midnight
        if (windowStart > windowEnd)
        {
            // e.g., 17:00 - 05:00 = hour >= 17 OR hour < 5
            return utcHour >= windowStart || utcHour < windowEnd;
        }
        else
        {
            // e.g., 13:00 - 22:00 = hour >= 13 AND hour < 22
            return utcHour >= windowStart && utcHour < windowEnd;
        }
    }
}
