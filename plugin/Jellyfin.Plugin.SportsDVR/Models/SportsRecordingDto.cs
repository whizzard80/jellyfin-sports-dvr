using System;

namespace Jellyfin.Plugin.SportsDVR.Models;

/// <summary>
/// DTO for sports recording information.
/// </summary>
public class SportsRecordingDto
{
    /// <summary>
    /// Gets or sets the Jellyfin item ID.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the clean title (Team1 vs Team2 - Date).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the original EPG title.
    /// </summary>
    public string? OriginalTitle { get; set; }

    /// <summary>
    /// Gets or sets the file path.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Gets or sets the first team name.
    /// </summary>
    public string? Team1 { get; set; }

    /// <summary>
    /// Gets or sets the second team name.
    /// </summary>
    public string? Team2 { get; set; }

    /// <summary>
    /// Gets or sets the league name.
    /// </summary>
    public string? League { get; set; }

    /// <summary>
    /// Gets or sets the subscription name that matched this recording.
    /// </summary>
    public string? SubscriptionName { get; set; }

    /// <summary>
    /// Gets or sets the subscription ID that matched this recording.
    /// </summary>
    public string? SubscriptionId { get; set; }

    /// <summary>
    /// Gets or sets the recorded date.
    /// </summary>
    public DateTime RecordedDate { get; set; }

    /// <summary>
    /// Gets or sets the runtime in ticks (duration from Jellyfin).
    /// </summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>
    /// Gets or sets the file size.
    /// </summary>
    public long? FileSize { get; set; }

    /// <summary>
    /// Gets or sets the image URL (Jellyfin thumbnail).
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// Gets or sets the overview/description (spoilers removed).
    /// </summary>
    public string? Overview { get; set; }

    /// <summary>
    /// Gets or sets the channel name.
    /// </summary>
    public string? ChannelName { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin playback URL.
    /// </summary>
    public string? PlaybackUrl { get; set; }
}

/// <summary>
/// Response for file safety check.
/// </summary>
public class FileSafetyCheckDto
{
    /// <summary>
    /// Gets or sets the item ID.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets whether the file is safe to move.
    /// </summary>
    public bool IsSafeToMove { get; set; }

    /// <summary>
    /// Gets or sets the reason for the safety status.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file age in minutes.
    /// </summary>
    public double FileAgeMinutes { get; set; }
}

/// <summary>
/// Response for organization operation.
/// </summary>
public class OrganizeRecordingsResponse
{
    /// <summary>
    /// Gets or sets the number of files scanned.
    /// </summary>
    public int Scanned { get; set; }

    /// <summary>
    /// Gets or sets the number of files matched.
    /// </summary>
    public int Matched { get; set; }

    /// <summary>
    /// Gets or sets the number of files moved.
    /// </summary>
    public int Moved { get; set; }

    /// <summary>
    /// Gets or sets the number of files skipped.
    /// </summary>
    public int Skipped { get; set; }

    /// <summary>
    /// Gets or sets the number of files that failed to move.
    /// </summary>
    public int Failed { get; set; }

    /// <summary>
    /// Gets or sets any errors that occurred.
    /// </summary>
    public List<string> Errors { get; set; } = new();
}
