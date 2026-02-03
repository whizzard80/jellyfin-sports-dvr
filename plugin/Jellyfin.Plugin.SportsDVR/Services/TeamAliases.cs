using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SportsDVR.Services;

/// <summary>
/// Manages team name aliases and normalization for pattern matching.
/// Handles variations like "Manchester City" = "Man City" = "MCFC".
/// </summary>
public static class TeamAliases
{
    /// <summary>
    /// Common suffixes that can be stripped or added to team names.
    /// </summary>
    public static readonly string[] CommonSuffixes =
    {
        "FC", "F.C.", "A.F.C.", "AFC", "SC", "S.C.",
        "CF", "C.F.", "AC", "A.C.", "AS", "A.S.",
        "United", "City", "Town", "Rovers", "Wanderers",
        "Athletic", "Athletico", "Sporting"
    };

    /// <summary>
    /// Built-in team aliases. Key is the canonical name, values are aliases.
    /// </summary>
    private static readonly Dictionary<string, string[]> BuiltInAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // English Premier League
        ["Manchester City"] = new[] { "Man City", "MCFC", "Man. City", "Citizens" },
        ["Manchester United"] = new[] { "Man Utd", "Man United", "MUFC", "Man. Utd", "Man. United", "Red Devils" },
        ["Liverpool"] = new[] { "LFC", "Liverpool FC", "The Reds" },
        ["Chelsea"] = new[] { "CFC", "Chelsea FC", "The Blues" },
        ["Arsenal"] = new[] { "AFC", "Arsenal FC", "The Gunners" },
        ["Tottenham Hotspur"] = new[] { "Spurs", "Tottenham", "THFC" },
        ["Newcastle United"] = new[] { "Newcastle", "NUFC", "Magpies" },
        ["West Ham United"] = new[] { "West Ham", "Hammers", "WHUFC" },
        ["Aston Villa"] = new[] { "Villa", "AVFC" },
        ["Brighton & Hove Albion"] = new[] { "Brighton", "Seagulls" },
        ["Wolverhampton Wanderers"] = new[] { "Wolves", "Wolverhampton" },
        ["Nottingham Forest"] = new[] { "Forest", "NFFC" },
        ["Everton"] = new[] { "EFC", "Toffees" },
        ["Leicester City"] = new[] { "Leicester", "LCFC", "Foxes" },
        
        // Spanish La Liga
        ["Real Madrid"] = new[] { "Madrid", "Real", "Los Blancos", "RMCF" },
        ["Barcelona"] = new[] { "Barça", "Barca", "FCB", "FC Barcelona", "Blaugrana" },
        ["Atletico Madrid"] = new[] { "Atleti", "Atletico", "Atlético Madrid", "Atlético" },
        ["Sevilla"] = new[] { "Sevilla FC", "SFC" },
        ["Real Betis"] = new[] { "Betis" },
        ["Athletic Bilbao"] = new[] { "Athletic Club", "Bilbao" },
        ["Real Sociedad"] = new[] { "Sociedad", "La Real" },
        ["Villarreal"] = new[] { "Villarreal CF", "Yellow Submarine" },
        ["Valencia"] = new[] { "Valencia CF", "Los Che" },
        
        // German Bundesliga
        ["Bayern Munich"] = new[] { "Bayern", "FC Bayern", "Bayern München", "FCB" },
        ["Borussia Dortmund"] = new[] { "Dortmund", "BVB", "Borussia" },
        ["RB Leipzig"] = new[] { "Leipzig", "Red Bull Leipzig" },
        ["Bayer Leverkusen"] = new[] { "Leverkusen", "Bayer 04" },
        ["Eintracht Frankfurt"] = new[] { "Frankfurt", "Eintracht", "SGE" },
        ["Borussia Monchengladbach"] = new[] { "Gladbach", "Mönchengladbach", "BMG" },
        ["VfB Stuttgart"] = new[] { "Stuttgart", "VfB" },
        ["Werder Bremen"] = new[] { "Bremen", "Werder" },
        ["VfL Wolfsburg"] = new[] { "Wolfsburg" },
        ["SC Freiburg"] = new[] { "Freiburg" },
        
        // Italian Serie A
        ["Juventus"] = new[] { "Juve", "Juventus FC", "Old Lady", "Bianconeri" },
        ["AC Milan"] = new[] { "Milan", "Rossoneri", "ACM" },
        ["Inter Milan"] = new[] { "Inter", "Internazionale", "Nerazzurri", "FC Internazionale" },
        ["AS Roma"] = new[] { "Roma", "Giallorossi" },
        ["Napoli"] = new[] { "SSC Napoli", "Partenopei" },
        ["Lazio"] = new[] { "SS Lazio", "Biancocelesti" },
        ["Atalanta"] = new[] { "Atalanta BC", "Dea" },
        ["Fiorentina"] = new[] { "ACF Fiorentina", "Viola" },
        
        // French Ligue 1
        ["Paris Saint-Germain"] = new[] { "PSG", "Paris SG", "Paris" },
        ["Olympique Marseille"] = new[] { "Marseille", "OM" },
        ["Olympique Lyon"] = new[] { "Lyon", "OL" },
        ["AS Monaco"] = new[] { "Monaco" },
        ["Lille"] = new[] { "LOSC", "Lille OSC" },
        
        // NBA
        ["Los Angeles Lakers"] = new[] { "Lakers", "LA Lakers", "L.A. Lakers" },
        ["Los Angeles Clippers"] = new[] { "Clippers", "LA Clippers", "L.A. Clippers" },
        ["Golden State Warriors"] = new[] { "Warriors", "GSW", "Golden State" },
        ["Boston Celtics"] = new[] { "Celtics", "Boston" },
        ["Brooklyn Nets"] = new[] { "Nets", "Brooklyn" },
        ["New York Knicks"] = new[] { "Knicks", "NY Knicks" },
        ["Philadelphia 76ers"] = new[] { "76ers", "Sixers", "Philly" },
        ["Miami Heat"] = new[] { "Heat", "Miami" },
        ["Chicago Bulls"] = new[] { "Bulls", "Chicago" },
        ["Dallas Mavericks"] = new[] { "Mavs", "Mavericks", "Dallas" },
        ["Houston Rockets"] = new[] { "Rockets", "Houston" },
        ["San Antonio Spurs"] = new[] { "Spurs", "San Antonio" },
        ["Phoenix Suns"] = new[] { "Suns", "Phoenix" },
        ["Denver Nuggets"] = new[] { "Nuggets", "Denver" },
        ["Milwaukee Bucks"] = new[] { "Bucks", "Milwaukee" },
        ["Toronto Raptors"] = new[] { "Raptors", "Toronto" },
        ["Minnesota Timberwolves"] = new[] { "Timberwolves", "T-Wolves", "Wolves", "Minnesota" },
        ["Oklahoma City Thunder"] = new[] { "Thunder", "OKC" },
        ["Cleveland Cavaliers"] = new[] { "Cavs", "Cavaliers", "Cleveland" },
        ["Indiana Pacers"] = new[] { "Pacers", "Indiana" },
        ["Detroit Pistons"] = new[] { "Pistons", "Detroit" },
        
        // NHL
        ["Boston Bruins"] = new[] { "Bruins", "B's" },
        ["New York Rangers"] = new[] { "Rangers", "NY Rangers", "NYR" },
        ["New York Islanders"] = new[] { "Islanders", "Isles", "NYI" },
        ["Toronto Maple Leafs"] = new[] { "Maple Leafs", "Leafs", "Toronto" },
        ["Montreal Canadiens"] = new[] { "Canadiens", "Habs", "Montreal" },
        ["Chicago Blackhawks"] = new[] { "Blackhawks", "Hawks", "Chicago" },
        ["Detroit Red Wings"] = new[] { "Red Wings", "Wings", "Detroit" },
        ["Pittsburgh Penguins"] = new[] { "Penguins", "Pens", "Pittsburgh" },
        ["Philadelphia Flyers"] = new[] { "Flyers", "Philly" },
        ["Washington Capitals"] = new[] { "Capitals", "Caps", "Washington" },
        ["Tampa Bay Lightning"] = new[] { "Lightning", "Bolts", "Tampa" },
        ["Florida Panthers"] = new[] { "Panthers", "Florida", "Cats" },
        ["Colorado Avalanche"] = new[] { "Avalanche", "Avs", "Colorado" },
        ["Vegas Golden Knights"] = new[] { "Golden Knights", "Knights", "Vegas", "VGK" },
        ["Edmonton Oilers"] = new[] { "Oilers", "Edmonton" },
        ["Calgary Flames"] = new[] { "Flames", "Calgary" },
        ["Vancouver Canucks"] = new[] { "Canucks", "Vancouver", "Nucks" },
        
        // NFL
        ["New England Patriots"] = new[] { "Patriots", "Pats", "New England" },
        ["Dallas Cowboys"] = new[] { "Cowboys", "Dallas" },
        ["Green Bay Packers"] = new[] { "Packers", "Green Bay" },
        ["San Francisco 49ers"] = new[] { "49ers", "Niners", "SF 49ers" },
        ["Kansas City Chiefs"] = new[] { "Chiefs", "KC Chiefs" },
        ["Buffalo Bills"] = new[] { "Bills", "Buffalo" },
        ["Philadelphia Eagles"] = new[] { "Eagles", "Philly" },
        ["Miami Dolphins"] = new[] { "Dolphins", "Miami" },
        ["New York Giants"] = new[] { "Giants", "NY Giants", "NYG" },
        ["New York Jets"] = new[] { "Jets", "NY Jets", "NYJ" },
        ["Las Vegas Raiders"] = new[] { "Raiders", "Las Vegas", "LV Raiders" },
        ["Denver Broncos"] = new[] { "Broncos", "Denver" },
        ["Baltimore Ravens"] = new[] { "Ravens", "Baltimore" },
        ["Pittsburgh Steelers"] = new[] { "Steelers", "Pittsburgh" },
        ["Cleveland Browns"] = new[] { "Browns", "Cleveland" },
        ["Cincinnati Bengals"] = new[] { "Bengals", "Cincy" },
        ["Seattle Seahawks"] = new[] { "Seahawks", "Seattle", "Hawks" },
        ["Los Angeles Rams"] = new[] { "Rams", "LA Rams" },
        ["Los Angeles Chargers"] = new[] { "Chargers", "LA Chargers" },
        
        // MLB
        ["Boston Red Sox"] = new[] { "Red Sox", "Sox", "Boston" },
        ["New York Yankees"] = new[] { "Yankees", "Yanks", "NY Yankees", "NYY" },
        ["New York Mets"] = new[] { "Mets", "NY Mets", "NYM" },
        ["Los Angeles Dodgers"] = new[] { "Dodgers", "LA Dodgers" },
        ["Chicago Cubs"] = new[] { "Cubs", "Cubbies" },
        ["Chicago White Sox"] = new[] { "White Sox", "Chi Sox" },
        ["San Francisco Giants"] = new[] { "Giants", "SF Giants" },
        ["St. Louis Cardinals"] = new[] { "Cardinals", "Cards", "STL" },
        ["Philadelphia Phillies"] = new[] { "Phillies", "Phils" },
        ["Houston Astros"] = new[] { "Astros", "Houston" },
        ["Atlanta Braves"] = new[] { "Braves", "Atlanta" },
        ["Detroit Tigers"] = new[] { "Tigers", "Detroit" },
    };

    // Regex to strip common suffixes
    private static readonly Regex SuffixPattern = new(
        @"\s*\b(FC|F\.C\.|A\.F\.C\.|AFC|SC|S\.C\.|CF|C\.F\.|AC|A\.C\.|AS|A\.S\.)\b\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Normalizes a team name by removing common suffixes.
    /// </summary>
    public static string NormalizeName(string teamName)
    {
        if (string.IsNullOrWhiteSpace(teamName))
        {
            return teamName;
        }

        // Strip common suffixes
        var normalized = SuffixPattern.Replace(teamName, "").Trim();
        
        // Also try stripping from the beginning (e.g., "FC Barcelona" -> "Barcelona")
        normalized = Regex.Replace(normalized, @"^(FC|A\.?F\.?C\.?|SC|S\.C\.)\s+", "", RegexOptions.IgnoreCase).Trim();
        
        return normalized;
    }

    /// <summary>
    /// Gets all possible name variations for a team.
    /// </summary>
    public static IEnumerable<string> GetAliases(string teamName)
    {
        var normalized = NormalizeName(teamName);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            teamName,
            normalized
        };

        // Check built-in aliases (canonical name)
        if (BuiltInAliases.TryGetValue(teamName, out var builtIn))
        {
            foreach (var alias in builtIn)
            {
                aliases.Add(alias);
            }
        }

        // Check built-in aliases (normalized name)
        if (BuiltInAliases.TryGetValue(normalized, out builtIn))
        {
            foreach (var alias in builtIn)
            {
                aliases.Add(alias);
            }
        }

        // Check if this name is an alias of another canonical name
        foreach (var (canonical, aliasList) in BuiltInAliases)
        {
            if (aliasList.Contains(teamName, StringComparer.OrdinalIgnoreCase) ||
                aliasList.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                aliases.Add(canonical);
                foreach (var alias in aliasList)
                {
                    aliases.Add(alias);
                }
            }
        }

        // Add user-configured aliases
        var config = Plugin.Instance?.Configuration;
        if (config?.CustomAliases != null)
        {
            foreach (var customAlias in config.CustomAliases)
            {
                if (customAlias.CanonicalName.Equals(teamName, StringComparison.OrdinalIgnoreCase) ||
                    customAlias.CanonicalName.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                    customAlias.Aliases.Contains(teamName, StringComparer.OrdinalIgnoreCase) ||
                    customAlias.Aliases.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    aliases.Add(customAlias.CanonicalName);
                    foreach (var alias in customAlias.Aliases)
                    {
                        aliases.Add(alias);
                    }
                }
            }
        }

        return aliases;
    }

    /// <summary>
    /// Builds a regex pattern that matches any variation of a team name.
    /// </summary>
    public static string BuildMatchPattern(string teamName)
    {
        var aliases = GetAliases(teamName).ToList();
        
        // Escape special regex characters and join with OR
        var escapedAliases = aliases
            .Select(a => Regex.Escape(a))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return $"/({string.Join("|", escapedAliases)})/i";
    }

    /// <summary>
    /// Checks if two team names are equivalent (accounting for aliases).
    /// </summary>
    public static bool AreEquivalent(string name1, string name2)
    {
        if (string.IsNullOrWhiteSpace(name1) || string.IsNullOrWhiteSpace(name2))
        {
            return false;
        }

        // Direct match
        if (name1.Equals(name2, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Normalized match
        var norm1 = NormalizeName(name1);
        var norm2 = NormalizeName(name2);
        if (norm1.Equals(norm2, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check aliases
        var aliases1 = GetAliases(name1);
        return aliases1.Any(a => a.Equals(name2, StringComparison.OrdinalIgnoreCase) ||
                                  a.Equals(norm2, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the canonical (official) name for a team.
    /// </summary>
    public static string GetCanonicalName(string teamName)
    {
        var normalized = NormalizeName(teamName);

        // Check if it's already a canonical name
        if (BuiltInAliases.ContainsKey(teamName))
        {
            return teamName;
        }

        if (BuiltInAliases.ContainsKey(normalized))
        {
            return normalized;
        }

        // Check if it's an alias
        foreach (var (canonical, aliasList) in BuiltInAliases)
        {
            if (aliasList.Contains(teamName, StringComparer.OrdinalIgnoreCase) ||
                aliasList.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                return canonical;
            }
        }

        // Check user aliases
        var config = Plugin.Instance?.Configuration;
        if (config?.CustomAliases != null)
        {
            foreach (var customAlias in config.CustomAliases)
            {
                if (customAlias.Aliases.Contains(teamName, StringComparer.OrdinalIgnoreCase) ||
                    customAlias.Aliases.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    return customAlias.CanonicalName;
                }
            }
        }

        // Return the normalized name if no canonical found
        return normalized;
    }
}

/// <summary>
/// User-configurable team alias.
/// </summary>
public class CustomTeamAlias
{
    /// <summary>
    /// Gets or sets the canonical team name.
    /// </summary>
    public string CanonicalName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of aliases for this team.
    /// </summary>
    public string[] Aliases { get; set; } = Array.Empty<string>();
}
