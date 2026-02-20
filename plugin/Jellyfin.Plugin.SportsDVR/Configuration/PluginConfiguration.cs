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
        Subscriptions = new List<Subscription>();
        CustomAliases = new List<CustomTeamAlias>();
        ManagedTimerKeys = new List<string>();
        EnableAutoScheduling = true;
        EnableAliasMatching = true;
        DailyScanTime = "10:00";
    }

    /// <summary>
    /// Gets or sets the maximum number of concurrent recordings.
    /// Must match the Jellyfin Live TV tuner "Simultaneous stream limit" so we never
    /// schedule more recordings at once than the tuner allows. Capped at 1-20 in the scheduler.
    /// </summary>
    public int MaxConcurrentRecordings { get; set; }

    /// <summary>
    /// Gets or sets whether auto-scheduling from EPG is enabled.
    /// </summary>
    public bool EnableAutoScheduling { get; set; }

    /// <summary>
    /// Gets or sets the daily EPG scan time (24h format, e.g. "10:00").
    /// The plugin scans the full EPG once at this time each day and builds
    /// the optimized recording schedule for the next 24 hours.
    /// A second scan runs at startup to catch any changes.
    /// </summary>
    public string DailyScanTime { get; set; } = "10:00";

    /// <summary>
    /// Gets or sets how often to scan EPG for matching programs (minutes).
    /// Kept for backward compatibility — ignored when DailyScanTime is set.
    /// </summary>
    public int ScanIntervalMinutes { get; set; } = 30;

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
    /// Gets or sets the primary region for time-based filtering.
    /// This affects when live games are expected to air.
    /// Options: "USA", "Europe", "Asia", "Australia", "All" (no time filtering)
    /// Default: "USA"
    /// </summary>
    public string PrimaryRegion { get; set; } = "USA";

    /// <summary>
    /// Gets or sets whether to purge Jellyfin's Live TV guide cache once per day.
    /// When enabled, the plugin clears xmltv and *_channels cache so "Refresh Guide Data" loads fresh EPG
    /// (fixes stale "Game Complete" entries). Runs at most once every 24 hours during the hour set by GuideCachePurgeHour.
    /// </summary>
    public bool EnableDailyGuideCachePurge { get; set; } = true;

    /// <summary>
    /// Hour of day (0-23, server local time) when the guide cache purge is allowed to run.
    /// The purge runs when the EPG scan task runs during this hour (and at least 24h since last purge).
    /// Default 4 = 4 AM.
    /// </summary>
    public int GuideCachePurgeHour { get; set; } = 4;

    /// <summary>
    /// Gets or sets timer keys created by the plugin (name|channel|time).
    /// Used to track which Jellyfin timers belong to Sports DVR so we can
    /// identify and cancel them without relying on Jellyfin's Overview field.
    /// </summary>
    public List<string> ManagedTimerKeys { get; set; }

    /// <summary>
    /// Gets or sets channel numbers to skip during EPG scanning.
    /// Comma-separated, supports individual numbers and ranges.
    /// Example: "1-150,1664,1700-1800"
    /// </summary>
    public string IgnoredChannels { get; set; } = string.Empty;
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
            // USA: 10 AM - 1 AM EST = 15:00 - 06:00 UTC (covers NCAA daytime games + late West Coast)
            "USA" => (15, 6, 11, 21),  // Euro football: 6 AM - 4 PM EST = 11:00 - 21:00 UTC
            
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
