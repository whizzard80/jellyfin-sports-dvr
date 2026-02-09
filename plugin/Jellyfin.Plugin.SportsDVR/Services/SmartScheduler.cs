using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SportsDVR.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsDVR.Services;

/// <summary>
/// Smart scheduler that optimizes recording schedules based on:
/// - Connection limits (e.g., 2 concurrent streams)
/// - Subscription priorities
/// - Duplicate game detection (same game on multiple channels)
/// - Time slot conflicts
/// </summary>
public class SmartScheduler
{
    private readonly ILogger<SmartScheduler> _logger;
    private readonly AliasService _aliasService;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmartScheduler"/> class.
    /// </summary>
    public SmartScheduler(ILogger<SmartScheduler> logger, AliasService aliasService)
    {
        _logger = logger;
        _aliasService = aliasService;
    }

    /// <summary>
    /// Creates an optimized recording schedule from matched programs.
    /// </summary>
    /// <param name="matches">All programs that match subscriptions.</param>
    /// <param name="maxConcurrent">Maximum concurrent recordings (connection limit).</param>
    /// <returns>Optimized list of programs to record.</returns>
    public List<ScheduledRecording> CreateSchedule(
        List<MatchedProgram> matches,
        int maxConcurrent)
    {
        if (matches.Count == 0)
        {
            return new List<ScheduledRecording>();
        }

        _logger.LogInformation(
            "Creating optimized schedule from {Count} matched programs with {Max} max concurrent",
            matches.Count,
            maxConcurrent);

        // Step 1: Detect and group duplicate broadcasts (same game on multiple channels)
        var uniqueGames = DeduplicateGames(matches);
        _logger.LogInformation("After deduplication: {Count} unique games", uniqueGames.Count);

        // Step 2: Sort by priority (highest first) then by start time
        var sortedGames = uniqueGames
            .OrderByDescending(g => g.EffectivePriority)
            .ThenBy(g => g.Primary.Program.StartDate)
            .ToList();

        // Step 3: Build schedule respecting connection limits
        var schedule = BuildSchedule(sortedGames, maxConcurrent);

        _logger.LogInformation(
            "Final schedule: {Scheduled} recordings from {Total} unique games",
            schedule.Count,
            uniqueGames.Count);

        return schedule;
    }

    /// <summary>
    /// Groups programs that are the same game broadcast on different channels.
    /// </summary>
    private List<GameGroup> DeduplicateGames(List<MatchedProgram> matches)
    {
        var groups = new List<GameGroup>();

        foreach (var match in matches)
        {
            // Try to find an existing group for this game
            var existingGroup = groups.FirstOrDefault(g => IsSameGame(g.Primary, match));

            if (existingGroup != null)
            {
                // Add as alternate channel
                existingGroup.Alternates.Add(match);
                _logger.LogDebug(
                    "Added alternate channel for '{Game}': {Channel}",
                    match.Program.Name,
                    match.Program.ChannelName);
            }
            else
            {
                // Create new group
                groups.Add(new GameGroup
                {
                    Primary = match,
                    Alternates = new List<MatchedProgram>()
                });
            }
        }

        // For each group, pick the best primary channel
        foreach (var group in groups)
        {
            if (group.Alternates.Count > 0)
            {
                var allOptions = new List<MatchedProgram> { group.Primary };
                allOptions.AddRange(group.Alternates);

                // Pick best channel: prefer earliest start (live > replay),
                // then sports channels, then subscription priority
                var best = allOptions
                    .OrderBy(m => m.Program.StartDate)
                    .ThenByDescending(m => m.Scored.IsSportsChannel ? 1 : 0)
                    .ThenByDescending(m => m.Subscription.Priority)
                    .First();

                if (best != group.Primary)
                {
                    group.Alternates.Remove(best);
                    group.Alternates.Add(group.Primary);
                    group.Primary = best;
                }

                _logger.LogInformation(
                    "Game '{Game}' available on {Count} channels, selected: {Channel}",
                    group.Primary.Program.Name,
                    allOptions.Count,
                    group.Primary.Program.ChannelName);
            }
        }

        return groups;
    }

    /// <summary>
    /// Determines if two programs are the same game on different channels.
    /// For team sports: same teams within 24 hours = same game (later airing is a replay).
    /// For non-team events: uses title similarity within a shorter window.
    /// </summary>
    private bool IsSameGame(MatchedProgram a, MatchedProgram b)
    {
        // Check if same teams first (most reliable)
        var teamsA = GetNormalizedTeams(a);
        var teamsB = GetNormalizedTeams(b);

        if (teamsA.Count >= 2 && teamsB.Count >= 2)
        {
            // Both have two teams - check if same matchup
            if (teamsA.SetEquals(teamsB))
            {
                // Same teams within 15 hours = same game.
                // This catches simultaneous broadcasts on different channels
                // AND replays/rebroadcasts later the same day, while avoiding
                // false positives for back-to-back days (e.g., 7 PM → 2 PM next day = 19h).
                var timeDiff = Math.Abs((a.Program.StartDate - b.Program.StartDate).TotalHours);
                if (timeDiff <= 15)
                {
                    _logger.LogDebug(
                        "Same game detected: '{TitleA}' and '{TitleB}' ({TimeDiff:F1}h apart)",
                        a.Program.Name, b.Program.Name, timeDiff);
                    return true;
                }
            }
            // Different teams = different game
            return false;
        }

        // Try to extract teams from title if ScoredProgram didn't have them
        var extractedTeamsA = ExtractTeamsFromTitle(a.Program.Name ?? "");
        var extractedTeamsB = ExtractTeamsFromTitle(b.Program.Name ?? "");
        
        if (extractedTeamsA.Count >= 2 && extractedTeamsB.Count >= 2)
        {
            if (TeamsMatch(extractedTeamsA, extractedTeamsB))
            {
                // Same teams within 15 hours = same game
                var timeDiff = Math.Abs((a.Program.StartDate - b.Program.StartDate).TotalHours);
                if (timeDiff <= 15)
                {
                    _logger.LogDebug(
                        "Same game detected (title match): '{TitleA}' and '{TitleB}' ({TimeDiff:F1}h apart)",
                        a.Program.Name, b.Program.Name, timeDiff);
                    return true;
                }
            }
            return false;
        }

        // Fall back to title similarity for non-team sports (UFC, etc.)
        var titleA = NormalizeTitle(a.Program.Name ?? "");
        var titleB = NormalizeTitle(b.Program.Name ?? "");
        
        var similarity = CalculateSimilarity(titleA, titleB);
        if (similarity > 0.8) // Higher threshold for title-only matching
        {
            // For non-team events, use a 6-hour window (covers delayed feeds but not next-day replays)
            var timeDiff = Math.Abs((a.Program.StartDate - b.Program.StartDate).TotalHours);
            if (timeDiff <= 6)
            {
                _logger.LogDebug(
                    "Same event detected (similarity {Sim:P0}): '{TitleA}' and '{TitleB}' ({TimeDiff:F1}h apart)",
                    similarity, a.Program.Name, b.Program.Name, timeDiff);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts team names from a title using common patterns.
    /// </summary>
    private static List<string> ExtractTeamsFromTitle(string title)
    {
        var teams = new List<string>();
        
        // Remove ALL common prefixes and noise (may be multiple: "Live: NBA: ...")
        var cleaned = title;
        
        // Patterns to remove from ANYWHERE (not just start)
        var noisePatterns = new[] {
            @"\blive\s*[:\-]?\s*",
            @"\bnew\s*[:\-]?\s*",
            @"\bnba\s*[:\-]?\s*",
            @"\bnfl\s*[:\-]?\s*",
            @"\bnhl\s*[:\-]?\s*",
            @"\bmlb\s*[:\-]?\s*",
            @"\bpl\s*[:\-]?\s*",
            @"\bserie\s*a\s*[:\-]?\s*",
            @"\bmma\s*[:\-]?\s*",
            @"\bufc\s+\d+\s*[:\-]?\s*",       // UFC 325:
            @"\bcarabao\s*[:\-]?\s*",          // Carabao
            @"\bfa\s*cup\s*[:\-]?\s*",         // FA Cup
            @"\d{2}/\d{2}\s*[:\-]?\s*",        // 25/26:
            @"\bsf\d+\s*",                     // SF1, SF2 (semifinal)
            @"\bmw\d+\s*[:\-]?\s*",            // MW24: (matchweek)
        };
        
        foreach (var pattern in noisePatterns)
        {
            cleaned = Regex.Replace(cleaned, pattern, "", RegexOptions.IgnoreCase);
        }
        
        cleaned = cleaned.Trim();
        
        // Match "Team1 vs/v/@/at Team2"
        var match = Regex.Match(cleaned, @"(.+?)\s+(?:vs\.?|v\.?|@|at)\s+(.+?)(?:\s*[;\(\[]|$)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            teams.Add(NormalizeTeamName(match.Groups[1].Value));
            teams.Add(NormalizeTeamName(match.Groups[2].Value));
        }
        
        // Also handle UFC events: "UFC 325: Volkanovski vs. Lopes 2" → ["Volkanovski", "Lopes"]
        if (teams.Count == 0)
        {
            var ufcMatch = Regex.Match(title, @"UFC\s+\d+\s*:\s*(.+?)\s+(?:vs\.?|v\.?)\s+(.+?)(?:\s+\d+)?$", RegexOptions.IgnoreCase);
            if (ufcMatch.Success)
            {
                teams.Add(NormalizeTeamName(ufcMatch.Groups[1].Value));
                teams.Add(NormalizeTeamName(ufcMatch.Groups[2].Value));
            }
        }
        
        return teams;
    }

    /// <summary>
    /// Normalizes a team name for comparison.
    /// Removes city prefixes to match "Celtics" with "Boston Celtics".
    /// </summary>
    private static string NormalizeTeamName(string team)
    {
        var normalized = team
            .Trim()
            .ToLowerInvariant()
            .Replace(".", "")
            .Replace(",", "");
        
        // Remove common city prefixes for better matching
        // "Boston Celtics" → "celtics", "Dallas Mavericks" → "mavericks"
        var cityPrefixes = new[] {
            "boston", "dallas", "los angeles", "la", "new york", "ny",
            "golden state", "san antonio", "oklahoma city", "portland",
            "minnesota", "milwaukee", "miami", "chicago", "detroit",
            "cleveland", "atlanta", "charlotte", "washington", "toronto",
            "brooklyn", "philadelphia", "phoenix", "denver", "utah",
            "sacramento", "orlando", "indiana", "new orleans", "memphis",
            "houston", "san francisco", "seattle", "manchester", "arsenal",
            "chelsea", "liverpool", "tottenham", "brighton", "aston villa",
            "newcastle", "west ham", "everton", "nottingham", "leeds",
            "wolverhampton", "crystal palace", "brentford", "fulham",
            "ac", "inter", "juventus", "napoli", "roma", "lazio", "fiorentina",
            "real madrid", "barcelona", "atletico", "sevilla", "villarreal",
            "bayern", "borussia"
        };
        
        foreach (var prefix in cityPrefixes)
        {
            if (normalized.StartsWith(prefix + " "))
            {
                var remainder = normalized.Substring(prefix.Length).Trim();
                if (!string.IsNullOrEmpty(remainder))
                {
                    normalized = remainder;
                    break;
                }
            }
        }
        
        return normalized;
    }

    /// <summary>
    /// Checks if two team lists represent the same matchup.
    /// </summary>
    private bool TeamsMatch(List<string> teamsA, List<string> teamsB)
    {
        if (teamsA.Count < 2 || teamsB.Count < 2) return false;
        
        // Normalize using alias service
        var normA = teamsA.Select(t => _aliasService.ResolveAlias(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normB = teamsB.Select(t => _aliasService.ResolveAlias(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        return normA.SetEquals(normB);
    }

    /// <summary>
    /// Extracts and normalizes team names from a matched program.
    /// </summary>
    private HashSet<string> GetNormalizedTeams(MatchedProgram match)
    {
        var teams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(match.Scored.Team1))
        {
            teams.Add(_aliasService.ResolveAlias(match.Scored.Team1));
        }
        if (!string.IsNullOrEmpty(match.Scored.Team2))
        {
            teams.Add(_aliasService.ResolveAlias(match.Scored.Team2));
        }

        return teams;
    }

    /// <summary>
    /// Normalizes a title for comparison.
    /// </summary>
    private static string NormalizeTitle(string title)
    {
        // Remove common prefixes/suffixes and normalize
        return title
            .ToLowerInvariant()
            .Replace("live:", "")
            .Replace("(live)", "")
            .Replace("[live]", "")
            .Replace("new:", "")
            .Trim();
    }

    /// <summary>
    /// Calculates similarity between two strings (0-1).
    /// </summary>
    private static double CalculateSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return 0;
        }

        // Simple word overlap similarity
        var wordsA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wordsB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();

        return union > 0 ? (double)intersection / union : 0;
    }

    /// <summary>
    /// Builds an optimized schedule respecting connection limits.
    /// Checks for actual temporal overlap (accounting for full program duration)
    /// so that non-overlapping games on different time slots are not blocked.
    /// </summary>
    private List<ScheduledRecording> BuildSchedule(List<GameGroup> sortedGames, int maxConcurrent)
    {
        var schedule = new List<ScheduledRecording>();

        foreach (var game in sortedGames)
        {
            var program = game.Primary.Program;
            var startTime = program.StartDate;
            var endTime = program.EndDate;

            // Find all already-scheduled recordings that actually overlap with this game's
            // time window. Two recordings overlap when: existingStart < newEnd AND existingEnd > newStart.
            // This correctly handles games sorted by priority (not chronologically) and accounts
            // for full program duration, not just start times.
            var overlapping = schedule
                .Where(r => r.StartTime < endTime && r.EndTime > startTime)
                .ToList();

            if (overlapping.Count >= maxConcurrent)
            {
                // No capacity - check if we should preempt a lower priority recording
                var lowestPriority = overlapping
                    .OrderBy(r => r.Priority)
                    .FirstOrDefault();

                if (lowestPriority != null && lowestPriority.Priority < game.EffectivePriority)
                {
                    // Preempt lower priority recording
                    _logger.LogInformation(
                        "Preempting '{Low}' (pri {LowPri}) for '{High}' (pri {HighPri})",
                        lowestPriority.Title,
                        lowestPriority.Priority,
                        program.Name,
                        game.EffectivePriority);

                    schedule.Remove(lowestPriority);
                }
                else
                {
                    // Can't schedule - log conflict
                    _logger.LogWarning(
                        "Cannot schedule '{Game}' at {Start}-{End} - {Count} overlapping recordings (max {Max} concurrent)",
                        program.Name,
                        startTime.ToLocalTime().ToString("g"),
                        endTime.ToLocalTime().ToString("g"),
                        overlapping.Count,
                        maxConcurrent);
                    continue;
                }
            }

            // Schedule this game
            var recording = new ScheduledRecording
            {
                GameGroup = game,
                Program = program,
                Match = game.Primary,
                Title = program.Name ?? "Unknown",
                StartTime = startTime,
                EndTime = endTime,
                Priority = game.EffectivePriority,
                ChannelName = program.ChannelName ?? "Unknown",
                HasBackupChannels = game.Alternates.Count > 0
            };

            schedule.Add(recording);

            _logger.LogDebug(
                "Scheduled: {Title} at {Start}-{End} on {Channel} (priority {Priority}, {Overlap} concurrent)",
                recording.Title,
                recording.StartTime.ToLocalTime().ToString("g"),
                recording.EndTime.ToLocalTime().ToString("g"),
                recording.ChannelName,
                recording.Priority,
                overlapping.Count + 1);
        }

        return schedule;
    }
}

/// <summary>
/// A program that matched a subscription.
/// </summary>
public class MatchedProgram
{
    /// <summary>Gets or sets the program info.</summary>
    public ProgramInfo Program { get; set; } = null!;

    /// <summary>Gets or sets the scoring result.</summary>
    public ScoredProgram Scored { get; set; } = null!;

    /// <summary>Gets or sets the matching subscription.</summary>
    public Subscription Subscription { get; set; } = null!;
}

/// <summary>
/// A group of programs that are the same game on different channels.
/// </summary>
public class GameGroup
{
    /// <summary>Gets or sets the primary (best) channel.</summary>
    public MatchedProgram Primary { get; set; } = null!;

    /// <summary>Gets or sets alternate channels for the same game.</summary>
    public List<MatchedProgram> Alternates { get; set; } = new();

    /// <summary>
    /// Gets the effective priority (subscription priority + type bonus).
    /// Team subscriptions get a slight bonus over league subscriptions.
    /// </summary>
    public int EffectivePriority => Primary.Subscription.Priority +
        (Primary.Subscription.Type == SubscriptionType.Team ? 10 : 0);
}

/// <summary>
/// A scheduled recording in the optimized schedule.
/// </summary>
public class ScheduledRecording
{
    /// <summary>Gets or sets the game group.</summary>
    public GameGroup GameGroup { get; set; } = null!;

    /// <summary>Gets or sets the program to record.</summary>
    public ProgramInfo Program { get; set; } = null!;

    /// <summary>Gets or sets the matched program.</summary>
    public MatchedProgram Match { get; set; } = null!;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = "";

    /// <summary>Gets or sets the start time.</summary>
    public DateTime StartTime { get; set; }

    /// <summary>Gets or sets the end time.</summary>
    public DateTime EndDate { get; set; }

    /// <summary>Gets or sets the end time.</summary>
    public DateTime EndTime { get; set; }

    /// <summary>Gets or sets the priority.</summary>
    public int Priority { get; set; }

    /// <summary>Gets or sets the channel name.</summary>
    public string ChannelName { get; set; } = "";

    /// <summary>Gets or sets whether backup channels are available.</summary>
    public bool HasBackupChannels { get; set; }
}
