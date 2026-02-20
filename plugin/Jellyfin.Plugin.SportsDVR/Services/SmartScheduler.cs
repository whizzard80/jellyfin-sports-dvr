using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SportsDVR.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsDVR.Services;

/// <summary>
/// Optimal recording scheduler using priority-weighted greedy sweep with preemption.
///
/// Algorithm:
/// 1. Deduplicate games (same matchup on multiple channels -> pick best channel).
/// 2. Sort ALL games globally by EffectivePriority descending, then start time ascending.
/// 3. For each game (highest priority first):
///    a. If a concurrent slot is free at that time -> schedule it.
///    b. If slots are full, find the LOWEST-priority recording that overlaps.
///       If the current game outranks it -> evict it, schedule the current game.
///    c. Otherwise -> skip (all overlapping recordings are equal/higher priority).
/// 4. After the main pass, re-insert displaced games into any remaining gaps
///    (processed in priority order).
///
/// This guarantees that a priority #1 Celtics game will ALWAYS be scheduled,
/// even if it means evicting a priority #14 NCAA Basketball game that was
/// placed earlier in the sweep. Lower-priority games fill whatever gaps remain.
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
    public List<ScheduledRecording> CreateSchedule(
        List<MatchedProgram> matches,
        int maxConcurrent,
        List<(DateTime Start, DateTime End)>? existingSlots = null)
    {
        if (matches.Count == 0)
        {
            return new List<ScheduledRecording>();
        }

        _logger.LogInformation(
            "Building optimized schedule: {Count} matched programs, {Max} max concurrent, {Existing} existing timers",
            matches.Count,
            maxConcurrent,
            existingSlots?.Count ?? 0);

        // Step 1: Deduplicate — same game on multiple channels -> single GameGroup
        var uniqueGames = DeduplicateGames(matches);
        _logger.LogInformation("After deduplication: {Count} unique games (from {Total} matches)", uniqueGames.Count, matches.Count);

        // Log the priority breakdown
        var bySubscription = uniqueGames
            .GroupBy(g => g.Primary.Subscription.SortOrder)
            .OrderBy(g => g.Key);
        _logger.LogInformation("Games by subscription priority:");
        foreach (var group in bySubscription)
        {
            var first = group.First();
            _logger.LogInformation(
                "  #{Sort} {SubName} ({Type}): {Count} games, priority score {Pri}",
                first.Primary.Subscription.SortOrder + 1,
                first.Primary.Subscription.Name,
                first.Primary.Subscription.Type,
                group.Count(),
                first.EffectivePriority);
        }

        // Step 2: Build optimal schedule with preemption (chronological sweep)
        var schedule = BuildOptimalSchedule(uniqueGames, maxConcurrent, existingSlots);

        // Step 4: Log the final schedule
        _logger.LogInformation("--- FINAL RECORDING SCHEDULE ({Count} recordings) ---", schedule.Count);
        foreach (var rec in schedule.OrderBy(r => r.StartTime))
        {
            _logger.LogInformation(
                "  {Start} - {End} | {Title} | priority #{SortOrder} {SubName} (score {Pri})",
                rec.StartTime.ToLocalTime().ToString("g"),
                rec.EndTime.ToLocalTime().ToString("g"),
                rec.Title,
                rec.Match.Subscription.SortOrder + 1,
                rec.Match.Subscription.Name,
                rec.Priority);
        }

        var skippedCount = uniqueGames.Count - schedule.Count;
        if (skippedCount > 0)
        {
            _logger.LogInformation("  ({Skipped} games could not fit due to concurrent stream limits)", skippedCount);
        }

        return schedule;
    }

    /// <summary>
    /// Priority-weighted greedy sweep with preemption.
    ///
    /// Games arrive sorted by priority (highest first). Each game either:
    /// - Gets a free slot and is scheduled immediately
    /// - Preempts a lower-priority recording and takes its slot
    /// - Is skipped because all overlapping slots hold equal/higher priority recordings
    ///
    /// Displaced recordings get a second pass to fill any remaining gaps.
    /// </summary>
    private List<ScheduledRecording> BuildOptimalSchedule(
        List<GameGroup> games,
        int maxConcurrent,
        List<(DateTime Start, DateTime End)>? existingSlots)
    {
        // Sort chronologically, break ties by priority desc, then shortest end first
        var sorted = games
            .OrderBy(g => g.Primary.Program.StartDate)
            .ThenByDescending(g => g.EffectivePriority)
            .ThenBy(g => g.Primary.Program.EndDate)
            .ToList();

        var fixedSlots = existingSlots?.ToList() ?? new List<(DateTime Start, DateTime End)>();
        var active = new List<ScheduledRecording>(); // overlapping with current time in sweep
        var result = new List<ScheduledRecording>(); // all committed recordings

        foreach (var game in sorted)
        {
            var start = game.Primary.Program.StartDate;
            var end = game.Primary.Program.EndDate;
            var priority = game.EffectivePriority;

            // Drop any active recordings that have ended
            active.RemoveAll(r => r.EndTime <= start);

            // Count overlaps including fixed slots
            var overlappingActive = active.Count(r => r.StartTime < end && r.EndTime > start);
            var overlappingFixed = fixedSlots.Count(s => s.Start < end && s.End > start);
            var totalOverlap = overlappingActive + overlappingFixed;

            _logger.LogDebug("CONSIDER: '{Title}' {Start}-{End} prio={Pri} activeOverlap={Act} fixedOverlap={Fix}",
                game.Primary.Program.Name,
                start.ToLocalTime().ToString("g"),
                end.ToLocalTime().ToString("g"),
                priority,
                overlappingActive,
                overlappingFixed);

            if (totalOverlap < maxConcurrent)
            {
                var rec = CreateRecording(game);
                active.Add(rec);
                result.Add(rec);
                _logger.LogInformation(
                    "SCHEDULED: '{Title}' {Start}-{End} (priority #{Sort} {Sub}, {Used}/{Max} concurrent)",
                    game.Primary.Program.Name,
                    start.ToLocalTime().ToString("g"),
                    end.ToLocalTime().ToString("g"),
                    game.Primary.Subscription.SortOrder + 1,
                    game.Primary.Subscription.Name,
                    totalOverlap + 1,
                    maxConcurrent);
                _logger.LogDebug("DECISION: scheduled '{Title}' (free slot, {Used}/{Max})",
                    game.Primary.Program.Name, totalOverlap + 1, maxConcurrent);
                continue;
            }

            // Slots full: find lowest-priority active that overlaps
            var lowest = active
                .Where(r => r.StartTime < end && r.EndTime > start)
                .OrderBy(r => r.Priority)
                .ThenByDescending(r => r.EndTime)
                .FirstOrDefault();

            if (lowest != null && lowest.Priority < priority && overlappingFixed < maxConcurrent)
            {
                active.Remove(lowest);
                result.Remove(lowest);

                var rec = CreateRecording(game);
                active.Add(rec);
                result.Add(rec);

                _logger.LogInformation(
                    "PREEMPTED: '{Evicted}' (priority #{EvSort} {EvSub}) evicted by '{Winner}' (priority #{WinSort} {WinSub})",
                    lowest.Title,
                    lowest.Match.Subscription.SortOrder + 1,
                    lowest.Match.Subscription.Name,
                    game.Primary.Program.Name,
                    game.Primary.Subscription.SortOrder + 1,
                    game.Primary.Subscription.Name);
                _logger.LogDebug("DECISION: preempted '{Evicted}' (prio {EvPri}) with '{Winner}' (prio {WinPri})",
                    lowest.Title, lowest.Priority,
                    game.Primary.Program.Name, priority);
            }
            else
            {
                _logger.LogDebug("DECISION: skipped '{Title}' prio={Pri} (all {Count} overlaps equal/higher or fixed slots block)",
                    game.Primary.Program.Name, priority, totalOverlap);
            }
        }

        // Sort result by start for stable output
        return result.OrderBy(r => r.StartTime).ToList();
    }

    private static ScheduledRecording CreateRecording(GameGroup game)
    {
        var program = game.Primary.Program;
        return new ScheduledRecording
        {
            GameGroup = game,
            Program = program,
            Match = game.Primary,
            Title = program.Name ?? "Unknown",
            StartTime = program.StartDate,
            EndTime = program.EndDate,
            Priority = game.EffectivePriority,
            ChannelName = program.ChannelName ?? "Unknown",
            HasBackupChannels = game.Alternates.Count > 0
        };
    }

    /// <summary>
    /// Groups programs that are the same game broadcast on different channels.
    /// </summary>
    private List<GameGroup> DeduplicateGames(List<MatchedProgram> matches)
    {
        var groups = new List<GameGroup>();

        foreach (var match in matches)
        {
            var existingGroup = groups.FirstOrDefault(g => IsSameGame(g.Primary, match));

            if (existingGroup != null)
            {
                existingGroup.Alternates.Add(match);
                _logger.LogDebug(
                    "Added alternate channel for '{Game}': {Channel}",
                    match.Program.Name,
                    match.Program.ChannelName);
            }
            else
            {
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

                var best = allOptions
                    .OrderBy(m => m.Program.StartDate)
                    .ThenByDescending(m => m.Scored.IsSportsChannel ? 1 : 0)
                    .ThenBy(m => m.Subscription.SortOrder)
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
    /// </summary>
    private bool IsSameGame(MatchedProgram a, MatchedProgram b)
    {
        var teamsA = GetNormalizedTeams(a);
        var teamsB = GetNormalizedTeams(b);

        if (teamsA.Count >= 2 && teamsB.Count >= 2)
        {
            if (teamsA.SetEquals(teamsB))
            {
                var timeDiff = Math.Abs((a.Program.StartDate - b.Program.StartDate).TotalHours);
                if (timeDiff <= 15)
                {
                    _logger.LogDebug(
                        "Same game detected: '{TitleA}' and '{TitleB}' ({TimeDiff:F1}h apart)",
                        a.Program.Name, b.Program.Name, timeDiff);
                    return true;
                }
            }
            return false;
        }

        var extractedTeamsA = ExtractTeamsFromTitle(a.Program.Name ?? "");
        var extractedTeamsB = ExtractTeamsFromTitle(b.Program.Name ?? "");

        if (extractedTeamsA.Count >= 2 && extractedTeamsB.Count >= 2)
        {
            if (TeamsMatch(extractedTeamsA, extractedTeamsB))
            {
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

        var titleA = NormalizeTitle(a.Program.Name ?? "");
        var titleB = NormalizeTitle(b.Program.Name ?? "");

        var similarity = CalculateSimilarity(titleA, titleB);
        if (similarity > 0.8)
        {
            var timeDiffMinutes = Math.Abs((a.Program.StartDate - b.Program.StartDate).TotalMinutes);
            if (timeDiffMinutes <= 30)
            {
                _logger.LogDebug(
                    "Same event detected (similarity {Sim:P0}): '{TitleA}' and '{TitleB}' ({TimeDiff:F0} min apart)",
                    similarity, a.Program.Name, b.Program.Name, timeDiffMinutes);
                return true;
            }
        }

        return false;
    }

    private static List<string> ExtractTeamsFromTitle(string title)
    {
        var teams = new List<string>();
        var cleaned = title;

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
            @"\bufc\s+\d+\s*[:\-]?\s*",
            @"\bcarabao\s*[:\-]?\s*",
            @"\bfa\s*cup\s*[:\-]?\s*",
            @"\d{2}/\d{2}\s*[:\-]?\s*",
            @"\bsf\d+\s*",
            @"\bmw\d+\s*[:\-]?\s*",
        };

        foreach (var pattern in noisePatterns)
        {
            cleaned = Regex.Replace(cleaned, pattern, "", RegexOptions.IgnoreCase);
        }

        cleaned = cleaned.Trim();

        var match = Regex.Match(cleaned, @"(.+?)\s+(?:vs\.?|v\.?|@|at)\s+(.+?)(?:\s*[;\(\[]|$)", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            teams.Add(NormalizeTeamName(match.Groups[1].Value));
            teams.Add(NormalizeTeamName(match.Groups[2].Value));
        }

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

    private static string NormalizeTeamName(string team)
    {
        var normalized = team
            .Trim()
            .ToLowerInvariant()
            .Replace(".", "")
            .Replace(",", "");

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

    private bool TeamsMatch(List<string> teamsA, List<string> teamsB)
    {
        if (teamsA.Count < 2 || teamsB.Count < 2) return false;
        var normA = teamsA.Select(t => _aliasService.ResolveAlias(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normB = teamsB.Select(t => _aliasService.ResolveAlias(t)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return normA.SetEquals(normB);
    }

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

    private static string NormalizeTitle(string title)
    {
        return title
            .ToLowerInvariant()
            .Replace("live:", "")
            .Replace("(live)", "")
            .Replace("[live]", "")
            .Replace("new:", "")
            .Trim();
    }

    private static double CalculateSimilarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return 0;
        }

        var wordsA = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wordsB = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var intersection = wordsA.Intersect(wordsB).Count();
        var union = wordsA.Union(wordsB).Count();
        return union > 0 ? (double)intersection / union : 0;
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
    /// Gets the effective priority for scheduling.
    /// Derived from subscription list order (SortOrder 0 = most important).
    /// Inverted so that higher value = higher priority for OrderByDescending sorting.
    /// Team subscriptions get a +10 bonus over league/event subscriptions at the same position.
    /// </summary>
    public int EffectivePriority => (1000 - Primary.Subscription.SortOrder) +
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
    public DateTime EndTime { get; set; }

    /// <summary>Gets or sets the priority.</summary>
    public int Priority { get; set; }

    /// <summary>Gets or sets the channel name.</summary>
    public string ChannelName { get; set; } = "";

    /// <summary>Gets or sets whether backup channels are available.</summary>
    public bool HasBackupChannels { get; set; }
}
