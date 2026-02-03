using System;

namespace Jellyfin.Plugin.SportsDVR.Models;

/// <summary>
/// A parsed EPG program with extracted sports information.
/// </summary>
public class ParsedProgram
{
    /// <summary>
    /// Gets or sets the original program ID from Jellyfin.
    /// </summary>
    public string ProgramId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the channel ID.
    /// </summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the original title from EPG.
    /// </summary>
    public string OriginalTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cleaned title for display.
    /// </summary>
    public string CleanTitle { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the first team/participant (if detected).
    /// </summary>
    public string? Team1 { get; set; }

    /// <summary>
    /// Gets or sets the second team/participant (if detected).
    /// </summary>
    public string? Team2 { get; set; }

    /// <summary>
    /// Gets or sets the detected league or event series.
    /// </summary>
    public string? League { get; set; }

    /// <summary>
    /// Gets or sets whether this appears to be a live broadcast.
    /// </summary>
    public bool IsLive { get; set; }

    /// <summary>
    /// Gets or sets whether this appears to be a replay/encore.
    /// </summary>
    public bool IsReplay { get; set; }

    /// <summary>
    /// Gets or sets whether this appears to be a sports program.
    /// </summary>
    public bool IsSports { get; set; }

    /// <summary>
    /// Gets or sets the program start time.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Gets or sets the program end time.
    /// </summary>
    public DateTime EndTime { get; set; }

    /// <summary>
    /// Gets or sets the EPG category (e.g., "Sports", "Entertainment").
    /// </summary>
    public string? Category { get; set; }
}
