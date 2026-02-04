using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsDVR.Services;

/// <summary>
/// Confidence-based scorer that evaluates EPG programs to determine
/// likelihood of being a live sports game. Uses multiple signals.
/// </summary>
public class SportsScorer
{
    private readonly ILogger<SportsScorer> _logger;

    // Score weights
    private const int SCORE_VS_PATTERN = 50;
    private const int SCORE_AT_PATTERN = 45;
    private const int SCORE_V_PATTERN = 40;
    private const int SCORE_TWO_TEAMS_SAME_SPORT = 40;
    private const int SCORE_SPORTS_CHANNEL = 25;
    private const int SCORE_VENUE_WITH_MATCHUP = 20;
    private const int SCORE_SPORTS_CATEGORY = 15;
    private const int SCORE_LEAGUE_IN_TITLE = 12;
    private const int SCORE_ONE_KNOWN_TEAM = 8;

    private const int PENALTY_REPLAY = -35;
    private const int PENALTY_NON_SPORTS_CHANNEL = -20;
    private const int PENALTY_NON_SPORTS_PATTERN = -50;
    private const int PENALTY_SINGLE_ENTITY = -25;
    private const int PENALTY_PREGAME_POSTGAME = -15;

    public const int THRESHOLD_LIKELY_GAME = 50;
    public const int THRESHOLD_POSSIBLE_GAME = 30;

    // Pre-compiled patterns for performance
    private static readonly Regex VsPattern = new(@"\s+(vs\.?|versus)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AtSymbolPattern = new(@"\s+@\s+", RegexOptions.Compiled);
    private static readonly Regex AtWordPattern = new(@"\s+at\s+(?=[A-Z])", RegexOptions.Compiled);
    private static readonly Regex VPattern = new(@"\s+v\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReplayPattern = new(@"\b(replay|classic|encore|rerun|vintage|throwback)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex YearPrefixPattern = new(@"^(19[5-9]\d|20[0-2]\d)\s+", RegexOptions.Compiled);
    // Detect archived content with year in parentheses: "(2008)", "(2012)", etc.
    private static readonly Regex ArchivedYearPattern = new(@"\((19[89]\d|20[0-2]\d)\)\s*$", RegexOptions.Compiled);
    // "Next game:" placeholder programs
    private static readonly Regex PlaceholderPattern = new(@"^Next\s+game:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Highlights and mini shows - not actual games
    private static readonly Regex HighlightsPattern = new(@"\b(mini|highlights?|\bhl\b|recap|review|round-?up|best\s+of|top\s+\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Past season format - NOT current season. Current season (25/26) should NOT be flagged.
    // Only flag clearly past seasons like 23/24, 22/23, etc.
    private static readonly Regex PastSeasonPattern = new(@"\b(1[89]|20|21|22|23|24)[/-](1[89]|20|21|22|23|24|25)\s*:", RegexOptions.Compiled);
    private static readonly Regex NonSportsPattern = new(@"\b(news|weather|forecast|talk\s*show|documentary|movie|film)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PregamePattern = new(@"\b(pregame|postgame|halftime|preview|highlights|analysis|studio|report)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    
    // UFC numbered events: "UFC 299", "UFC 300: Main Card", etc.
    private static readonly Regex UfcNumberedPattern = new(@"\bUFC\s+(\d{2,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LiveIndicatorPattern = new(@"\b(live|new)\b|\(live\)|\[live\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // UFC non-event content to exclude - interviews, previews, flashbacks, etc.
    private static readonly Regex UfcNonEventPattern = new(
        @"\b(countdown|embedded|preview|weigh-in|press\s*conference|ultimate\s*fighter|dana\s*white|" +
        @"flashback|reloaded|on\s+the\s+line|connected|honors|awards|" +
        @"тойм|클래식|review|story|stories|" +  // Foreign language recaps
        @"previews?|breakdown|react|wants\s+revenge|" +  // Interview/analysis content
        @"full\s+fight|fight\s+night\s+\d|ufc\s+\d+\s*[-:]\s*\w+\s+v\s+\w+\s+\(\d{4}\))\b",  // Past fights with years
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Live UFC event indicators - what we WANT to record
    private static readonly Regex UfcLiveEventPattern = new(
        @"\b(main\s*card|prelims?|early\s*prelims?|fight\s*night.*live|ufc\s+\d{3}.*live)\b|" +
        @"^live[:\s].*ufc|ufc.*\blive\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    
    private const int SCORE_UFC_NUMBERED = 45;         // UFC 299, UFC 300, etc.
    private const int SCORE_LIVE_INDICATOR = 10;       // "LIVE" tag bonus
    private static readonly Regex TeamExtractVs = new(@"^(.+?)\s+(?:vs\.?|versus)\s+(.+?)(?:\s*[-–—\(\[\|]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TeamExtractAt = new(@"^(.+?)\s+(?:@|at)\s+(.+?)(?:\s*[-–—\(\[\|]|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LeaguePrefixPattern = new(
        @"^(?:live\s*[:\-]?\s*)?(?:(?:NBA|NFL|NHL|MLB|EPL|Premier League|La Liga|Bundesliga|Serie A|Champions League|Europa League|MLS|Ligue 1|NCAA|College)\s*[:\-]\s*)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Sports channels
    private static readonly string[] SportsChannels = {
        "espn", "fox sports", "fs1", "fs2", "nbc sports", "cbs sports", "tnt",
        "mlb network", "nfl network", "nba tv", "nhl network", "golf channel",
        "nesn", "msg", "yes network", "bein", "sky sports", "bt sport", "dazn"
    };

    private static readonly string[] NonSportsChannels = {
        "weather", "news", "cnn", "msnbc", "fox news", "hgtv", "food network",
        "discovery", "tlc", "lifetime", "hallmark", "comedy central", "cartoon", "disney"
    };

    // Teams by league (key teams for matching)
    private static readonly Dictionary<string, HashSet<string>> TeamsByLeague = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NBA"] = new(StringComparer.OrdinalIgnoreCase) {
            "celtics", "lakers", "warriors", "bulls", "heat", "knicks", "nets", "76ers", "sixers",
            "mavericks", "nuggets", "suns", "bucks", "clippers", "grizzlies", "pelicans", "rockets",
            "spurs", "raptors", "thunder", "pacers", "cavaliers", "pistons", "hawks", "hornets",
            "magic", "timberwolves", "trail blazers", "blazers", "kings", "jazz", "wizards"
        },
        ["NFL"] = new(StringComparer.OrdinalIgnoreCase) {
            "patriots", "cowboys", "packers", "chiefs", "49ers", "eagles", "bills", "ravens",
            "dolphins", "jets", "giants", "steelers", "browns", "bengals", "raiders", "chargers",
            "broncos", "seahawks", "rams", "cardinals", "saints", "buccaneers", "falcons", "panthers",
            "bears", "lions", "vikings", "titans", "colts", "texans", "jaguars", "commanders"
        },
        ["NHL"] = new(StringComparer.OrdinalIgnoreCase) {
            "bruins", "rangers", "maple leafs", "canadiens", "penguins", "blackhawks", "red wings",
            "flyers", "capitals", "lightning", "avalanche", "oilers", "flames", "canucks", "sharks",
            "ducks", "kings", "blues", "wild", "predators", "jets", "stars", "hurricanes", "devils",
            "islanders", "sabres", "senators", "panthers", "kraken", "golden knights", "blue jackets"
        },
        ["MLB"] = new(StringComparer.OrdinalIgnoreCase) {
            "yankees", "red sox", "dodgers", "cubs", "giants", "mets", "cardinals", "braves",
            "astros", "phillies", "padres", "white sox", "reds", "brewers", "twins", "angels",
            "mariners", "rangers", "blue jays", "rays", "royals", "athletics", "tigers", "guardians",
            "orioles", "pirates", "nationals", "rockies", "marlins", "diamondbacks"
        },
        ["EPL"] = new(StringComparer.OrdinalIgnoreCase) {
            "arsenal", "chelsea", "liverpool", "manchester city", "man city", "manchester united",
            "man utd", "tottenham", "spurs", "newcastle", "west ham", "aston villa", "brighton",
            "fulham", "brentford", "crystal palace", "everton", "wolves", "nottingham forest",
            "bournemouth", "luton", "burnley", "sheffield united"
        },
        ["LaLiga"] = new(StringComparer.OrdinalIgnoreCase) {
            "barcelona", "real madrid", "atletico madrid", "sevilla", "valencia", "villarreal",
            "real betis", "athletic bilbao", "real sociedad", "getafe", "celta vigo", "osasuna"
        },
        ["Bundesliga"] = new(StringComparer.OrdinalIgnoreCase) {
            "bayern munich", "bayern", "borussia dortmund", "dortmund", "rb leipzig", "leverkusen",
            "frankfurt", "wolfsburg", "gladbach", "freiburg", "hoffenheim", "stuttgart"
        },
        ["SerieA"] = new(StringComparer.OrdinalIgnoreCase) {
            "juventus", "inter milan", "inter", "ac milan", "milan", "napoli", "roma", "lazio",
            "atalanta", "fiorentina", "torino", "bologna", "sassuolo"
        },
        ["UFC"] = new(StringComparer.OrdinalIgnoreCase) { "ufc" },
        ["F1"] = new(StringComparer.OrdinalIgnoreCase) { "formula 1", "f1", "grand prix" }
    };

    // League detection keywords
    private static readonly Dictionary<string, string[]> LeagueKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NBA"] = new[] { "nba" },
        ["NFL"] = new[] { "nfl" },
        ["NHL"] = new[] { "nhl" },
        ["MLB"] = new[] { "mlb" },
        ["EPL"] = new[] { "epl", "premier league" },
        ["LaLiga"] = new[] { "la liga" },
        ["Bundesliga"] = new[] { "bundesliga" },
        ["SerieA"] = new[] { "serie a" },
        ["Champions League"] = new[] { "champions league", "ucl" },
        ["UFC"] = new[] { "ufc" },
        ["F1"] = new[] { "formula 1", "f1" }
    };

    public SportsScorer(ILogger<SportsScorer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Scores a program to determine likelihood of being a sports game.
    /// </summary>
    public ScoredProgram Score(string title, string? channel = null, string? description = null, bool hasSportsCategory = false)
    {
        var result = new ScoredProgram { OriginalTitle = title, Channel = channel };
        int score = 0;
        var titleLower = title.ToLowerInvariant();
        var channelLower = channel?.ToLowerInvariant() ?? "";

        // Clean league prefix before team extraction
        var cleanedTitle = LeaguePrefixPattern.Replace(title, "").Trim();

        // Check matchup patterns (strongest signals)
        if (VsPattern.IsMatch(title))
        {
            score += SCORE_VS_PATTERN;
            result.HasMatchupPattern = true;
            ExtractTeams(TeamExtractVs, cleanedTitle, result);
        }
        else if (AtSymbolPattern.IsMatch(title) || AtWordPattern.IsMatch(title))
        {
            score += SCORE_AT_PATTERN;
            result.HasMatchupPattern = true;
            ExtractTeams(TeamExtractAt, cleanedTitle, result);
        }
        else if (VPattern.IsMatch(title))
        {
            score += SCORE_V_PATTERN;
            result.HasMatchupPattern = true;
        }

        // Check for known teams
        var (league1, team1) = FindTeam(titleLower);
        if (team1 != null)
        {
            var remaining = titleLower.Replace(team1.ToLower(), "");
            var (league2, team2) = FindTeam(remaining);

            if (team2 != null && league1 == league2)
            {
                score += SCORE_TWO_TEAMS_SAME_SPORT;
                result.DetectedLeague = league1;
                result.Team1 ??= team1;
                result.Team2 ??= team2;
            }
            else if (team2 == null && !result.HasMatchupPattern)
            {
                score += SCORE_ONE_KNOWN_TEAM + PENALTY_SINGLE_ENTITY;
            }
            else
            {
                score += SCORE_ONE_KNOWN_TEAM;
            }
        }

        // Check channel
        if (SportsChannels.Any(c => channelLower.Contains(c)))
        {
            score += SCORE_SPORTS_CHANNEL;
            result.IsSportsChannel = true;
        }
        else if (NonSportsChannels.Any(c => channelLower.Contains(c)))
        {
            score += PENALTY_NON_SPORTS_CHANNEL;
        }

        // Check category
        if (hasSportsCategory)
        {
            score += SCORE_SPORTS_CATEGORY;
        }

        // Check league in title
        if (result.DetectedLeague == null)
        {
            foreach (var (league, keywords) in LeagueKeywords)
            {
                if (keywords.Any(k => titleLower.Contains(k)))
                {
                    result.DetectedLeague = league;
                    score += SCORE_LEAGUE_IN_TITLE;
                    break;
                }
            }
        }

        // UFC numbered events (UFC 299, UFC 300, etc.) - special handling
        var ufcMatch = UfcNumberedPattern.Match(title);
        if (ufcMatch.Success && !UfcNonEventPattern.IsMatch(title))
        {
            score += SCORE_UFC_NUMBERED;
            result.DetectedLeague = "UFC";
            result.CleanTitle = $"UFC {ufcMatch.Groups[1].Value}";
            
            // Bonus for "LIVE" indicator
            if (LiveIndicatorPattern.IsMatch(title))
            {
                score += SCORE_LIVE_INDICATOR;
            }
        }

        // Negative signals - Replay/Archive detection
        if (ReplayPattern.IsMatch(title) || YearPrefixPattern.IsMatch(title) || ArchivedYearPattern.IsMatch(title))
        {
            score += PENALTY_REPLAY;
            result.IsReplay = true;
        }
        
        // Highlights, minis, recaps - NOT actual games
        if (HighlightsPattern.IsMatch(title))
        {
            score += PENALTY_REPLAY;
            result.IsReplay = true;
        }
        
        // Past season format indicates replay: "NBA 23/24: Team v Team"
        // Current season (25/26) is NOT penalized
        if (PastSeasonPattern.IsMatch(title))
        {
            score += PENALTY_REPLAY;
            result.IsReplay = true;
        }
        
        // Placeholder programs (e.g., "Next game: Team vs Team at...")
        if (PlaceholderPattern.IsMatch(title))
        {
            score += PENALTY_NON_SPORTS_PATTERN;
            result.IsReplay = true; // Treat as non-live content
        }

        if (NonSportsPattern.IsMatch(title))
        {
            score += PENALTY_NON_SPORTS_PATTERN;
        }

        if (PregamePattern.IsMatch(title))
        {
            score += PENALTY_PREGAME_POSTGAME;
            result.IsPregamePostgame = true;
        }

        result.Score = score;
        result.IsLikelyGame = score >= THRESHOLD_LIKELY_GAME && !result.IsPregamePostgame;
        result.IsPossibleGame = score >= THRESHOLD_POSSIBLE_GAME;

        // Generate clean title
        if (result.Team1 != null && result.Team2 != null)
        {
            result.CleanTitle = $"{result.Team1} vs {result.Team2}";
        }
        else
        {
            result.CleanTitle = title.Trim();
        }

        return result;
    }

    private void ExtractTeams(Regex pattern, string title, ScoredProgram result)
    {
        var match = pattern.Match(title);
        if (match.Success)
        {
            result.Team1 = CleanTeamName(match.Groups[1].Value);
            result.Team2 = CleanTeamName(match.Groups[2].Value);
        }
    }

    private (string? league, string? team) FindTeam(string text)
    {
        foreach (var (league, teams) in TeamsByLeague)
        {
            foreach (var team in teams.OrderByDescending(t => t.Length))
            {
                if (text.Contains(team.ToLower()))
                {
                    return (league, team);
                }
            }
        }
        return (null, null);
    }

    private static string CleanTeamName(string name)
    {
        var cleaned = Regex.Replace(name.Trim(), @"\s*\([^)]*\)", "");
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        return cleaned.Trim();
    }
}

/// <summary>
/// Result of scoring a program.
/// </summary>
public class ScoredProgram
{
    public string OriginalTitle { get; set; } = "";
    public string? Channel { get; set; }
    public string? CleanTitle { get; set; }
    public string? Team1 { get; set; }
    public string? Team2 { get; set; }
    public string? DetectedLeague { get; set; }
    public int Score { get; set; }
    public bool IsLikelyGame { get; set; }
    public bool IsPossibleGame { get; set; }
    public bool IsReplay { get; set; }
    public bool HasMatchupPattern { get; set; }
    public bool IsSportsChannel { get; set; }
    public bool IsPregamePostgame { get; set; }
}
