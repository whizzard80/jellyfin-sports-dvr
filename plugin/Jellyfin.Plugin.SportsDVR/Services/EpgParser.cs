using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SportsDVR.Models;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsDVR.Services;

/// <summary>
/// Parses EPG program data to extract sports information.
/// </summary>
public class EpgParser
{
    private readonly ILogger<EpgParser> _logger;

    // Pattern to detect team matchups: "Team1 vs Team2", "Team1 @ Team2", "Team1 v Team2"
    // Note: "at" is only matched when followed by a capital letter to avoid "News at 5PM"
    private static readonly Regex MatchupPattern = new(
        @"^(.+?)\s+(?:vs\.?|v\.?|@)\s+(.+?)(?:\s*[-–—:]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pattern for "at" separator - more strict to avoid "Boston News at Noon"
    // Requires: "Team1 (record) at Team2" OR "Team1 at Team2" where Team2 starts with capital
    private static readonly Regex TeamarrMatchupPattern = new(
        @"^(.+?)\s+(?:\([^)]+\)\s+)?at\s+([A-Z][A-Za-z].+?)(?:\s*\([^)]+\))?$",
        RegexOptions.Compiled);

    // Alternative pattern for hyphen-separated matchups: "Team1-Team2", "Team1 - Team2"
    // Common in European football EPGs - requires both sides to be 2+ words or known team patterns
    private static readonly Regex HyphenMatchupPattern = new(
        @"^([A-Z][A-Za-z]+(?:\s+[A-Za-z]+)+)\s*[-–—]\s*([A-Z][A-Za-z]+(?:\s+[A-Za-z]+)*)$",
        RegexOptions.Compiled);

    // Words that indicate NOT a sports matchup when found in "Team1-Team2" pattern
    private static readonly HashSet<string> NonSportsWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "All", "Access", "Off", "Natural", "Health", "News", "Morning", "Noon", "Night",
        "Day", "Week", "Report", "Show", "Live", "Today", "Tonight", "Class", "World"
    };

    // Pattern to extract league/event prefix: "NBA: Lakers vs Warriors", "2015 Fenway Hurling: Galway vs Dublin"
    private static readonly Regex LeaguePrefixPattern = new(
        @"^([^:]+?):\s*(.+)$",
        RegexOptions.Compiled);

    // Pattern to detect year at start of title (indicates classic/replay)
    private static readonly Regex YearPrefixPattern = new(
        @"^(19[5-9]\d|20[0-2]\d)\s+",
        RegexOptions.Compiled);

    // Words indicating a replay/encore
    private static readonly string[] ReplayIndicators =
    {
        "replay", "encore", "classic", "rerun", "re-air",
        "rebroadcast", "throwback", "vintage", "best of",
        "greatest", "memorable", "historic"
    };

    // Words indicating live broadcast
    private static readonly string[] LiveIndicators =
    {
        "live", "(live)", "[live]", "• live", "🔴"
    };

    // Known sports categories in EPG
    private static readonly string[] SportsCategories =
    {
        "sports", "sport", "athletics", "basketball", "football",
        "soccer", "baseball", "hockey", "tennis", "golf", "boxing",
        "mma", "wrestling", "racing", "motorsports"
    };

    // Known leagues and their aliases
    private static readonly Dictionary<string, string[]> KnownLeagues = new()
    {
        ["NBA"] = new[] { "nba", "basketball" },
        ["NFL"] = new[] { "nfl", "football", "american football" },
        ["MLB"] = new[] { "mlb", "baseball" },
        ["NHL"] = new[] { "nhl", "hockey" },
        ["MLS"] = new[] { "mls", "major league soccer" },
        ["Premier League"] = new[] { "premier league", "epl", "english premier" },
        ["La Liga"] = new[] { "la liga", "laliga", "spanish league" },
        ["Bundesliga"] = new[] { "bundesliga", "german league" },
        ["Serie A"] = new[] { "serie a", "italian league" },
        ["Ligue 1"] = new[] { "ligue 1", "french league" },
        ["Champions League"] = new[] { "champions league", "ucl", "uefa champions" },
        ["UFC"] = new[] { "ufc", "ultimate fighting", "mma" },
        ["WWE"] = new[] { "wwe", "wrestling", "smackdown", "raw", "nxt" },
        ["F1"] = new[] { "formula 1", "f1", "grand prix" },
        ["NASCAR"] = new[] { "nascar", "stock car" },
        ["PGA"] = new[] { "pga", "golf", "pga tour" },
        ["ATP"] = new[] { "atp", "tennis" },
        ["WTA"] = new[] { "wta", "women's tennis" },
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="EpgParser"/> class.
    /// </summary>
    public EpgParser(ILogger<EpgParser> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Parses a Jellyfin program into extracted sports information.
    /// </summary>
    public ParsedProgram Parse(BaseItemDto program)
    {
        var title = program.Name ?? string.Empty;
        var parsed = new ParsedProgram
        {
            ProgramId = program.Id,
            ChannelId = program.ChannelId ?? string.Empty,
            OriginalTitle = title,
            StartTime = program.StartDate ?? DateTime.UtcNow,
            EndTime = program.EndDate ?? DateTime.UtcNow.AddHours(3),
        };

        // Check EPG category
        if (program.Genres != null)
        {
            foreach (var genre in program.Genres)
            {
                if (IsSportsCategory(genre))
                {
                    parsed.IsSports = true;
                    parsed.Category = genre;
                    break;
                }
            }
        }

        // Detect replay - check both title content and year prefix
        parsed.IsReplay = IsReplay(title) || YearPrefixPattern.IsMatch(title);

        // Detect live
        parsed.IsLive = IsLive(title) || (program.IsLive ?? false);

        // Extract league/event prefix if present (e.g., "NBA: Lakers vs Warriors" or "2015 Fenway Hurling: Galway vs Dublin")
        var workingTitle = title;
        var leagueMatch = LeaguePrefixPattern.Match(title);
        if (leagueMatch.Success)
        {
            var potentialLeague = leagueMatch.Groups[1].Value.Trim();
            var normalizedLeague = NormalizeLeague(potentialLeague);
            if (normalizedLeague != null)
            {
                parsed.League = normalizedLeague;
            }
            // Always use the part after the colon for matchup detection
            // This handles "2015 Fenway Hurling: Galway vs Dublin" correctly
            workingTitle = leagueMatch.Groups[2].Value.Trim();
        }

        // Try to extract teams from matchup pattern (vs, @, v - most reliable)
        var matchup = MatchupPattern.Match(workingTitle);
        if (matchup.Success)
        {
            parsed.Team1 = CleanTeamName(matchup.Groups[1].Value);
            parsed.Team2 = CleanTeamName(matchup.Groups[2].Value);
            parsed.CleanTitle = $"{parsed.Team1} vs {parsed.Team2}";
            parsed.IsSports = true; // If it has vs pattern, likely sports
        }
        else
        {
            // Try Teamarr-style "at" pattern (stricter - requires capital letter or record)
            var teamarrMatchup = TeamarrMatchupPattern.Match(workingTitle);
            if (teamarrMatchup.Success)
            {
                var team1 = CleanTeamName(teamarrMatchup.Groups[1].Value);
                var team2 = CleanTeamName(teamarrMatchup.Groups[2].Value);
                
                // Additional validation: team2 shouldn't be a time like "Noon", "5PM", etc.
                if (!IsLikelyTime(team2) && !NonSportsWords.Contains(team2.Split(' ')[0]))
                {
                    parsed.Team1 = team1;
                    parsed.Team2 = team2;
                    parsed.CleanTitle = $"{parsed.Team1} vs {parsed.Team2}";
                    parsed.IsSports = true;
                }
                else
                {
                    parsed.CleanTitle = CleanTitle(workingTitle);
                }
            }
            else
            {
                // Try hyphen-separated pattern (common in European EPGs)
                var hyphenMatchup = HyphenMatchupPattern.Match(workingTitle);
                if (hyphenMatchup.Success && IsValidHyphenMatchup(hyphenMatchup.Groups[1].Value, hyphenMatchup.Groups[2].Value))
                {
                    parsed.Team1 = CleanTeamName(hyphenMatchup.Groups[1].Value);
                    parsed.Team2 = CleanTeamName(hyphenMatchup.Groups[2].Value);
                    parsed.CleanTitle = $"{parsed.Team1} vs {parsed.Team2}";
                    parsed.IsSports = true;
                }
                else
                {
                    parsed.CleanTitle = CleanTitle(workingTitle);
                }
            }
        }

        // Try to detect league from title if not found
        if (parsed.League == null)
        {
            parsed.League = DetectLeagueFromTitle(title);
        }

        // Mark as sports if we detected a league
        if (parsed.League != null)
        {
            parsed.IsSports = true;
        }

        return parsed;
    }

    /// <summary>
    /// Extracts suggested subscription names from a parsed program.
    /// </summary>
    public List<SuggestedSubscription> GetSuggestedSubscriptions(ParsedProgram program)
    {
        var suggestions = new List<SuggestedSubscription>();

        // Suggest teams if detected
        if (!string.IsNullOrEmpty(program.Team1))
        {
            suggestions.Add(new SuggestedSubscription
            {
                Name = program.Team1,
                Type = SubscriptionType.Team,
                SuggestedPattern = program.Team1.ToLowerInvariant()
            });
        }

        if (!string.IsNullOrEmpty(program.Team2))
        {
            suggestions.Add(new SuggestedSubscription
            {
                Name = program.Team2,
                Type = SubscriptionType.Team,
                SuggestedPattern = program.Team2.ToLowerInvariant()
            });
        }

        // Suggest league if detected
        if (!string.IsNullOrEmpty(program.League))
        {
            suggestions.Add(new SuggestedSubscription
            {
                Name = program.League,
                Type = SubscriptionType.League,
                SuggestedPattern = GetLeaguePattern(program.League)
            });
        }

        return suggestions;
    }

    private static bool IsSportsCategory(string category)
    {
        var lower = category.ToLowerInvariant();
        foreach (var sports in SportsCategories)
        {
            if (lower.Contains(sports))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsReplay(string title)
    {
        var lower = title.ToLowerInvariant();
        foreach (var indicator in ReplayIndicators)
        {
            if (lower.Contains(indicator))
            {
                return true;
            }
        }

        // Check for year patterns that might indicate old games (e.g., "1995 Finals")
        if (Regex.IsMatch(title, @"\b(19[5-9]\d|200\d|201\d|202[0-4])\b"))
        {
            // If year is more than 1 year ago, likely a classic/replay
            return true;
        }

        return false;
    }

    private static bool IsLive(string title)
    {
        var lower = title.ToLowerInvariant();
        foreach (var indicator in LiveIndicators)
        {
            if (lower.Contains(indicator))
            {
                return true;
            }
        }
        return false;
    }

    private static string? NormalizeLeague(string text)
    {
        var lower = text.ToLowerInvariant().Trim();
        foreach (var (league, aliases) in KnownLeagues)
        {
            foreach (var alias in aliases)
            {
                if (lower.Contains(alias))
                {
                    return league;
                }
            }
        }
        return null;
    }

    private static string? DetectLeagueFromTitle(string title)
    {
        var lower = title.ToLowerInvariant();
        foreach (var (league, aliases) in KnownLeagues)
        {
            foreach (var alias in aliases)
            {
                if (lower.Contains(alias))
                {
                    return league;
                }
            }
        }
        return null;
    }

    private static string GetLeaguePattern(string league)
    {
        if (KnownLeagues.TryGetValue(league, out var aliases))
        {
            return string.Join("|", aliases);
        }
        return league.ToLowerInvariant();
    }

    private static string CleanTeamName(string name)
    {
        // Remove common suffixes and clean up
        var cleaned = name.Trim();
        cleaned = Regex.Replace(cleaned, @"\s*\([^)]*\)\s*$", ""); // Remove trailing (...)
        cleaned = Regex.Replace(cleaned, @"\s*\[[^\]]*\]\s*$", ""); // Remove trailing [...]
        cleaned = cleaned.Trim();

        // Try to get canonical name if alias matching is enabled
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableAliasMatching == true)
        {
            var canonical = TeamAliases.GetCanonicalName(cleaned);
            if (!string.IsNullOrEmpty(canonical))
            {
                return canonical;
            }
        }

        return cleaned;
    }

    private static bool IsLikelyTime(string text)
    {
        // Check if text looks like a time: "Noon", "5PM", "10AM", "Night", etc.
        if (string.IsNullOrEmpty(text)) return false;
        var lower = text.ToLowerInvariant();
        return lower == "noon" || lower == "night" || lower == "midnight" ||
               Regex.IsMatch(text, @"^\d{1,2}[AP]M$", RegexOptions.IgnoreCase) ||
               Regex.IsMatch(text, @"^\d{1,2}:\d{2}");
    }

    private static bool IsValidHyphenMatchup(string team1, string team2)
    {
        // Validate that both sides of a hyphen pattern look like team names
        if (string.IsNullOrEmpty(team1) || string.IsNullOrEmpty(team2)) return false;

        // Check for non-sports words
        var words1 = team1.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var words2 = team2.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // At least one side should have 2+ words for European team names
        // e.g., "Manchester City" not "All-Access"
        if (words1.Length < 2 && words2.Length < 2) return false;

        // Check if any word is in the non-sports list
        foreach (var word in words1.Concat(words2))
        {
            if (NonSportsWords.Contains(word)) return false;
        }

        return true;
    }

    private static string CleanTitle(string title)
    {
        var cleaned = title;
        // Remove (LIVE), [LIVE], etc.
        cleaned = Regex.Replace(cleaned, @"\s*[\(\[]?live[\)\]]?\s*", " ", RegexOptions.IgnoreCase);
        // Remove (Replay), etc.
        cleaned = Regex.Replace(cleaned, @"\s*[\(\[]?replay[\)\]]?\s*", " ", RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }
}

/// <summary>
/// A suggested subscription based on parsed program content.
/// </summary>
public class SuggestedSubscription
{
    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the subscription type.
    /// </summary>
    public SubscriptionType Type { get; set; }

    /// <summary>
    /// Gets or sets the suggested match pattern.
    /// </summary>
    public string SuggestedPattern { get; set; } = string.Empty;
}

/// <summary>
/// DTO for Jellyfin program data (simplified).
/// </summary>
public class BaseItemDto
{
    /// <summary>
    /// Gets or sets the program ID.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the program name/title.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the channel ID.
    /// </summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// Gets or sets the start date.
    /// </summary>
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// Gets or sets the end date.
    /// </summary>
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Gets or sets the genres/categories.
    /// </summary>
    public string[]? Genres { get; set; }

    /// <summary>
    /// Gets or sets whether this is live.
    /// </summary>
    public bool? IsLive { get; set; }
}
