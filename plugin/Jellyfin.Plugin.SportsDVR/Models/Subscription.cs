using System;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SportsDVR.Models;

/// <summary>
/// Type of subscription.
/// </summary>
public enum SubscriptionType
{
    /// <summary>
    /// Subscribe to a specific team (requires vs/@ pattern in title).
    /// </summary>
    Team,

    /// <summary>
    /// Subscribe to a league (e.g., NBA, Premier League).
    /// </summary>
    League,

    /// <summary>
    /// Subscribe to an event series (e.g., UFC, WWE, F1).
    /// </summary>
    Event
}

/// <summary>
/// A subscription to automatically record matching sports events.
/// </summary>
public class Subscription
{
    /// <summary>
    /// Gets or sets the unique identifier.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets or sets the display name (e.g., "Lakers", "UFC", "Premier League").
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the subscription type.
    /// </summary>
    [JsonPropertyName("type")]
    public SubscriptionType Type { get; set; } = SubscriptionType.Team;

    /// <summary>
    /// Gets or sets the match pattern (regex supported).
    /// Example: "lakers" or "/la lakers|los angeles lakers/i".
    /// </summary>
    [JsonPropertyName("matchPattern")]
    public string MatchPattern { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets exclusion patterns (won't record if these match).
    /// Example: "countdown" to exclude "UFC Countdown" shows.
    /// </summary>
    [JsonPropertyName("excludePatterns")]
    public string[] ExcludePatterns { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the sort order (0-based position in the subscription list).
    /// Lower number = higher priority. Position 0 is most important.
    /// </summary>
    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }

    /// <summary>
    /// Gets or sets whether to include replays/encores.
    /// Default false - only record live/first-run.
    /// </summary>
    [JsonPropertyName("includeReplays")]
    public bool IncludeReplays { get; set; }

    /// <summary>
    /// Gets or sets whether the subscription is enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets when the subscription was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
