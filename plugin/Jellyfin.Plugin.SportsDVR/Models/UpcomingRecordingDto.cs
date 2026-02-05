using System;

namespace Jellyfin.Plugin.SportsDVR.Models;

/// <summary>
/// DTO for an upcoming scheduled recording.
/// </summary>
public class UpcomingRecordingDto
{
    /// <summary>
    /// Gets or sets the program ID.
    /// </summary>
    public string ProgramId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the clean title (team vs team format).
    /// </summary>
    public string CleanTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets team 1 name.
    /// </summary>
    public string? Team1 { get; set; }

    /// <summary>
    /// Gets or sets team 2 name.
    /// </summary>
    public string? Team2 { get; set; }

    /// <summary>
    /// Gets or sets the detected league.
    /// </summary>
    public string? League { get; set; }

    /// <summary>
    /// Gets or sets the channel name.
    /// </summary>
    public string ChannelName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the start time (UTC).
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Gets or sets the end time (UTC).
    /// </summary>
    public DateTime EndTime { get; set; }

    /// <summary>
    /// Gets or sets the subscription name that matched.
    /// </summary>
    public string SubscriptionName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the subscription type.
    /// </summary>
    public string SubscriptionType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the priority.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Gets or sets whether backup channels are available.
    /// </summary>
    public bool HasBackupChannels { get; set; }

    /// <summary>
    /// Gets or sets the number of backup channels.
    /// </summary>
    public int BackupChannelCount { get; set; }

    /// <summary>
    /// Gets or sets whether this recording was skipped due to conflicts.
    /// </summary>
    public bool IsSkipped { get; set; }

    /// <summary>
    /// Gets or sets the skip reason (if skipped).
    /// </summary>
    public string? SkipReason { get; set; }
}

/// <summary>
/// Response containing upcoming scheduled recordings.
/// </summary>
public class UpcomingRecordingsResponse
{
    /// <summary>
    /// Gets or sets the list of scheduled recordings.
    /// </summary>
    public System.Collections.Generic.List<UpcomingRecordingDto> Scheduled { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of skipped recordings (due to conflicts).
    /// </summary>
    public System.Collections.Generic.List<UpcomingRecordingDto> Skipped { get; set; } = new();

    /// <summary>
    /// Gets or sets the total count of scheduled recordings.
    /// </summary>
    public int TotalScheduled { get; set; }

    /// <summary>
    /// Gets or sets the total count of skipped recordings.
    /// </summary>
    public int TotalSkipped { get; set; }

    /// <summary>
    /// Gets or sets the maximum concurrent recordings allowed.
    /// </summary>
    public int MaxConcurrent { get; set; }

    /// <summary>
    /// Gets or sets when the last EPG scan occurred.
    /// </summary>
    public DateTime? LastScanTime { get; set; }

    /// <summary>
    /// Gets or sets when the next EPG scan will occur.
    /// </summary>
    public DateTime? NextScanTime { get; set; }

    /// <summary>
    /// Gets or sets summary by date (e.g., "Today: 3, Tomorrow: 5").
    /// </summary>
    public System.Collections.Generic.Dictionary<string, int> SummaryByDate { get; set; } = new();
}
