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
    // Case-insensitive "at" with word boundary — works on lowercased text too
    private static readonly Regex AtWordPattern = new(@"\s+at\s+\w", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
    // Pregame/postgame — excludes "highlights" (already penalized separately by HighlightsPattern)
    private static readonly Regex PregamePattern = new(@"\b(pregame|pre-game|postgame|post-game|halftime|half-time|preview|analysis|studio|report|pre-match|post-match|warmup|warm-up)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    
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

    // Teams by league — used for dedup (same-league team detection) and scoring bonus.
    // Use nicknames since Teamarr sends full names ("Chicago Bulls at Boston Celtics") 
    // and Contains() matches substrings. Multi-word nicknames work ("red sox", "golden knights").
    private static readonly Dictionary<string, HashSet<string>> TeamsByLeague = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NBA"] = new(StringComparer.OrdinalIgnoreCase) {
            "celtics", "lakers", "warriors", "bulls", "heat", "knicks", "nets", "76ers", "sixers",
            "mavericks", "mavs", "nuggets", "suns", "bucks", "clippers", "grizzlies", "pelicans",
            "rockets", "spurs", "raptors", "thunder", "pacers", "cavaliers", "cavs", "pistons",
            "hawks", "hornets", "magic", "timberwolves", "wolves", "trail blazers", "blazers",
            "kings", "jazz", "wizards"
        },
        ["WNBA"] = new(StringComparer.OrdinalIgnoreCase) {
            "aces", "dream", "fever", "liberty", "lynx", "mercury", "mystics", "sky", "sparks",
            "storm", "sun", "wings", "valkyries", "unrivaled"
        },
        ["NFL"] = new(StringComparer.OrdinalIgnoreCase) {
            "patriots", "cowboys", "packers", "chiefs", "49ers", "niners", "eagles", "bills",
            "ravens", "dolphins", "jets", "giants", "steelers", "browns", "bengals", "raiders",
            "chargers", "broncos", "seahawks", "rams", "cardinals", "saints", "buccaneers", "bucs",
            "falcons", "panthers", "bears", "lions", "vikings", "titans", "colts", "texans",
            "jaguars", "jags", "commanders"
        },
        ["NHL"] = new(StringComparer.OrdinalIgnoreCase) {
            "bruins", "rangers", "maple leafs", "canadiens", "habs", "penguins", "pens",
            "blackhawks", "red wings", "flyers", "capitals", "caps", "lightning", "bolts",
            "avalanche", "avs", "oilers", "flames", "canucks", "sharks", "ducks", "kings",
            "blues", "wild", "predators", "preds", "jets", "stars", "hurricanes", "canes",
            "devils", "islanders", "isles", "sabres", "senators", "sens", "panthers", "kraken",
            "golden knights", "blue jackets", "utah hockey club"
        },
        ["MLB"] = new(StringComparer.OrdinalIgnoreCase) {
            "yankees", "red sox", "dodgers", "cubs", "giants", "mets", "cardinals", "cards",
            "braves", "astros", "stros", "phillies", "padres", "white sox", "reds", "brewers",
            "twins", "angels", "mariners", "rangers", "blue jays", "jays", "rays", "royals",
            "athletics", "tigers", "guardians", "orioles", "pirates", "nationals", "nats",
            "rockies", "marlins", "diamondbacks", "dbacks"
        },
        ["MLS"] = new(StringComparer.OrdinalIgnoreCase) {
            "inter miami", "lafc", "la galaxy", "galaxy", "atlanta united", "sounders",
            "timbers", "sporting kc", "real salt lake", "nashville sc", "austin fc",
            "columbus crew", "crew", "fc cincinnati", "charlotte fc", "cf montreal",
            "new york red bulls", "red bulls", "nycfc", "new england revolution",
            "revs", "dc united", "orlando city", "toronto fc", "minnesota united",
            "houston dynamo", "dynamo", "colorado rapids", "rapids", "st louis city",
            "vancouver whitecaps", "san jose earthquakes", "philadelphia union",
            "san diego fc"
        },
        ["EPL"] = new(StringComparer.OrdinalIgnoreCase) {
            "arsenal", "chelsea", "liverpool", "manchester city", "man city",
            "manchester united", "man utd", "man united", "tottenham", "spurs",
            "newcastle", "newcastle united", "west ham", "aston villa", "brighton",
            "fulham", "brentford", "crystal palace", "everton", "wolves",
            "wolverhampton", "nottingham forest", "bournemouth", "leicester",
            "ipswich", "southampton"
        },
        ["LaLiga"] = new(StringComparer.OrdinalIgnoreCase) {
            "barcelona", "barca", "real madrid", "atletico madrid", "atletico",
            "sevilla", "valencia", "villarreal", "real betis", "betis",
            "athletic bilbao", "bilbao", "real sociedad", "sociedad", "getafe",
            "celta vigo", "osasuna", "mallorca", "rayo vallecano", "girona",
            "las palmas", "alaves", "valladolid", "espanyol", "leganes"
        },
        ["Bundesliga"] = new(StringComparer.OrdinalIgnoreCase) {
            "bayern munich", "bayern", "borussia dortmund", "dortmund", "bvb",
            "rb leipzig", "leipzig", "bayer leverkusen", "leverkusen",
            "eintracht frankfurt", "frankfurt", "wolfsburg", "gladbach",
            "borussia monchengladbach", "freiburg", "hoffenheim", "stuttgart",
            "union berlin", "mainz", "werder bremen", "augsburg",
            "bochum", "heidenheim", "darmstadt", "st pauli", "holstein kiel"
        },
        ["SerieA"] = new(StringComparer.OrdinalIgnoreCase) {
            "juventus", "juve", "inter milan", "ac milan", "napoli", "roma",
            "as roma", "lazio", "ss lazio", "atalanta", "fiorentina", "torino",
            "bologna", "monza", "udinese", "empoli", "genoa", "cagliari",
            "lecce", "verona", "salernitana", "frosinone", "como", "parma", "venezia"
        },
        ["Ligue1"] = new(StringComparer.OrdinalIgnoreCase) {
            "psg", "paris saint-germain", "paris saint germain", "marseille",
            "olympique marseille", "lyon", "olympique lyonnais", "monaco",
            "as monaco", "lille", "lens", "rennes", "nice", "ogc nice",
            "strasbourg", "montpellier", "nantes", "toulouse", "brest",
            "reims", "lorient", "metz", "clermont", "le havre"
        },
        ["LigaMX"] = new(StringComparer.OrdinalIgnoreCase) {
            "club america", "america", "chivas", "guadalajara", "cruz azul",
            "monterrey", "rayados", "tigres", "uanl", "pumas", "unam",
            "santos laguna", "toluca", "leon", "atlas", "pachuca",
            "puebla", "tijuana", "xolos", "necaxa", "mazatlan",
            "queretaro", "juarez", "san luis"
        },
        ["UFC"] = new(StringComparer.OrdinalIgnoreCase) { "ufc" },
        ["F1"] = new(StringComparer.OrdinalIgnoreCase) { "formula 1", "f1", "grand prix" },
        ["NASCAR"] = new(StringComparer.OrdinalIgnoreCase) { "nascar", "daytona", "talladega" }
    };

    // League detection keywords — includes Gracenote category strings ({gracenote_category})
    // that Teamarr puts in the title. Must be lowercase for comparison.
    private static readonly Dictionary<string, string[]> LeagueKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NBA"] = new[] { "nba", "nba basketball" },
        ["WNBA"] = new[] { "wnba", "wnba basketball", "women's nba" },
        ["NFL"] = new[] { "nfl", "nfl football" },
        ["NHL"] = new[] { "nhl", "nhl hockey", "ice hockey" },
        ["MLB"] = new[] { "mlb", "mlb baseball" },
        ["NCAA"] = new[] { "ncaa", "ncaam", "ncaaw", "ncaab",
            "college basketball", "college football", "college hockey", "college baseball",
            "college softball", "college soccer", "college lacrosse", "college volleyball",
            "men's college basketball", "women's college basketball",
            "men's college hockey", "women's college hockey",
            "march madness", "college world series" },
        ["EPL"] = new[] { "epl", "premier league", "english premier league",
            "premier league soccer", "english premier league soccer" },
        ["LaLiga"] = new[] { "la liga", "spanish la liga", "la liga soccer" },
        ["Bundesliga"] = new[] { "bundesliga", "german bundesliga", "bundesliga soccer" },
        ["SerieA"] = new[] { "serie a", "italian serie a", "serie a soccer" },
        ["Ligue1"] = new[] { "ligue 1", "french ligue 1", "ligue 1 soccer" },
        ["Champions League"] = new[] { "champions league", "ucl",
            "uefa champions league", "uefa champions league soccer" },
        ["Europa League"] = new[] { "europa league", "uefa europa league",
            "uefa europa league soccer" },
        ["Conference League"] = new[] { "conference league",
            "europa conference league", "uefa europa conference league" },
        ["MLS"] = new[] { "mls", "major league soccer", "mls soccer" },
        ["LigaMX"] = new[] { "liga mx", "mexican liga mx", "liga mx soccer" },
        ["CONCACAF"] = new[] { "concacaf", "concacaf champions", "gold cup" },
        ["Copa America"] = new[] { "copa america", "copa américa" },
        ["AFC"] = new[] { "afc champions", "afc asian cup" },
        ["CFL"] = new[] { "cfl", "canadian football" },
        ["XFL"] = new[] { "xfl", "ufl" },
        ["NWSL"] = new[] { "nwsl", "national women's soccer" },
        ["UFC"] = new[] { "ufc" },
        ["Boxing"] = new[] { "boxing", "wbc", "wba", "ibf", "wbo", "professional boxing" },
        ["MMA"] = new[] { "mma", "bellator", "pfl", "one championship" },
        ["F1"] = new[] { "formula 1", "f1", "formula one" },
        ["NASCAR"] = new[] { "nascar", "nascar cup", "nascar xfinity", "nascar trucks" },
        ["IndyCar"] = new[] { "indycar", "indy 500", "indycar series" },
        ["WWE"] = new[] { "wwe", "wwe raw", "wwe smackdown", "wwe nxt" },
        ["AEW"] = new[] { "aew", "aew dynamite", "aew rampage", "aew collision" },
        ["Golf"] = new[] { "pga", "lpga", "pga tour", "liv golf", "the masters",
            "us open golf", "open championship", "pga championship" },
        ["Tennis"] = new[] { "atp", "wta", "tennis", "us open tennis",
            "wimbledon", "roland garros", "australian open" },
        ["Rugby"] = new[] { "rugby", "six nations", "rugby world cup",
            "super rugby", "premiership rugby" },
        ["Cricket"] = new[] { "cricket", "ipl", "t20", "test cricket",
            "cricket world cup", "the ashes" },
        ["Soccer"] = new[] { "soccer", "football" },
        ["World Cup"] = new[] { "world cup", "fifa world cup", "world cup qualifier" },
        ["Olympics"] = new[] { "olympics", "olympic", "winter olympics", "summer olympics" }
    };

    public SportsScorer(ILogger<SportsScorer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Scores a program to determine likelihood of being a sports game.
    /// </summary>
    /// <param name="title">Program title</param>
    /// <param name="channel">Channel name</param>
    /// <param name="description">Program description/overview - may include EpisodeTitle for matchup info</param>
    /// <param name="hasSportsCategory">Whether the program is categorized as sports</param>
    public ScoredProgram Score(string title, string? channel = null, string? description = null, bool hasSportsCategory = false)
    {
        var result = new ScoredProgram { OriginalTitle = title, Channel = channel };
        int score = 0;
        var titleLower = title.ToLowerInvariant();
        var channelLower = channel?.ToLowerInvariant() ?? "";
        var descLower = description?.ToLowerInvariant() ?? "";
        
        // Combined text for searching (title + description covers raw EPG where matchup is in description)
        var combinedText = $"{titleLower} {descLower}";

        // Clean league prefix before team extraction
        var cleanedTitle = LeaguePrefixPattern.Replace(title, "").Trim();
        var cleanedDesc = LeaguePrefixPattern.Replace(description ?? "", "").Trim();

        // Check matchup patterns in BOTH title AND description (strongest signals)
        // For raw EPG, matchup might be in description like "BYU at Oklahoma State"
        if (VsPattern.IsMatch(title))
        {
            score += SCORE_VS_PATTERN;
            result.HasMatchupPattern = true;
            ExtractTeams(TeamExtractVs, cleanedTitle, result);
        }
        else if (VsPattern.IsMatch(description ?? ""))
        {
            score += SCORE_VS_PATTERN - 5;  // Slightly lower if in description
            result.HasMatchupPattern = true;
            ExtractTeams(TeamExtractVs, cleanedDesc, result);
        }
        else if (AtSymbolPattern.IsMatch(title) || AtWordPattern.IsMatch(title))
        {
            score += SCORE_AT_PATTERN;
            result.HasMatchupPattern = true;
            ExtractTeams(TeamExtractAt, cleanedTitle, result);
        }
        else if (AtSymbolPattern.IsMatch(description ?? "") || AtWordPattern.IsMatch(description ?? ""))
        {
            score += SCORE_AT_PATTERN - 5;  // Matchup in description
            result.HasMatchupPattern = true;
            ExtractTeams(TeamExtractAt, cleanedDesc, result);
        }
        else if (VPattern.IsMatch(title) || VPattern.IsMatch(description ?? ""))
        {
            score += SCORE_V_PATTERN;
            result.HasMatchupPattern = true;
        }

        // Check for known teams in combined text (title + description)
        var (league1, team1) = FindTeam(combinedText);
        if (team1 != null)
        {
            var remaining = combinedText.Replace(team1.ToLower(), "");
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

        // Check league in title OR description (combined text)
        if (result.DetectedLeague == null)
        {
            foreach (var (league, keywords) in LeagueKeywords)
            {
                if (keywords.Any(k => combinedText.Contains(k)))
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
