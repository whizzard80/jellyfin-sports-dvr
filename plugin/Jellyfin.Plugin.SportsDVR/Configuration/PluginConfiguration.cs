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
        RecordingServiceUrl = "http://localhost:8765";
        SportsLibraryPath = "/media/sports";
        RetentionDays = 3;
        MaxConcurrentRecordings = 2;
        EnableTimeShift = true;
        TeamarrUrl = string.Empty;
        DispatcharrUrl = string.Empty;
    }

    /// <summary>
    /// Gets or sets the URL of the recording service API.
    /// </summary>
    public string RecordingServiceUrl { get; set; }

    /// <summary>
    /// Gets or sets the path where sports recordings are stored.
    /// </summary>
    public string SportsLibraryPath { get; set; }

    /// <summary>
    /// Gets or sets the number of days to retain recordings before cleanup.
    /// </summary>
    public int RetentionDays { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of concurrent recordings.
    /// </summary>
    public int MaxConcurrentRecordings { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether time-shifting is enabled.
    /// </summary>
    public bool EnableTimeShift { get; set; }

    /// <summary>
    /// Gets or sets the Teamarr API URL for EPG data.
    /// </summary>
    public string TeamarrUrl { get; set; }

    /// <summary>
    /// Gets or sets the Dispatcharr API URL for stream data.
    /// </summary>
    public string DispatcharrUrl { get; set; }
}
