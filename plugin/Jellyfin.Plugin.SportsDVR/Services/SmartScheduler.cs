using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SportsDVR.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsDVR.Services;

/// <summary>
/// Smart scheduler that builds an optimized recording schedule.
/// 
/// Algorithm: Priority-tier maximization.
///
/// 1. Deduplicate games (same matchup on multiple channels → pick best channel).
/// 2. Group games into PRIORITY TIERS — one tier per subscription.
///    Within a tier, all games are equally important.
/// 3. Process tiers from highest to lowest priority. For each tier:
///    a. Find which time slots are still available (respecting maxConcurrent).
///    b. Use interval scheduling (earliest-end-first) to MAXIMIZE the number
///       of games from this tier that fit into the remaining slots.
/// 4. Higher-priority tiers always get first pick. Lower-priority tiers fill gaps.
///
/// This means: if you have Champions League at #3 and NCAA Basketball at #14,
/// all possible Champions League games are placed first. Then NCAA Basketball
/// games fill whatever slots remain — and within that tier, the scheduler
/// maximizes coverage by preferring games that end earliest (opening slots
/// for later games).
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

        // Step 1: Deduplicate — same game on multiple channels → single GameGroup
        var uniqueGames = DeduplicateGames(matches);
        _logger.LogInformation("After deduplication: {Count} unique games (from {Total} matches)", uniqueGames.Count, matches.Count);

        // Step 2: Group into priority tiers by subscription SortOrder.
        // Each subscription is its own tier. Games from the same subscription compete
        // only against each other for "which ones maximize coverage."
        var tiers = uniqueGames
            .GroupBy(g => g.Primary.Subscription.SortOrder)
            .OrderBy(g => g.Key) // SortOrder 0 = highest priority
            .Select(g => new PriorityTier
            {
                SortOrder = g.Key,
                SubscriptionName = g.First().Primary.Subscription.Name,
                SubscriptionType = g.First().Primary.Subscription.Type,
                Games = g.ToList()
            })
            .ToList();

        _logger.LogInformation("Organized into {Count} priority tiers:", tiers.Count);
        foreach (var tier in tiers)
        {
            _logger.LogInformation(
                "  Tier #{Sort}: {SubName} ({Type}) — {Count} games",
                tier.SortOrder + 1,
                tier.SubscriptionName,
                tier.SubscriptionType,
                tier.Games.Count);
        }

        // Step 3: Build schedule tier by tier
        var schedule = BuildScheduleByTiers(tiers, maxConcurrent, existingSlots);

        // Step 4: Log the final schedule
        _logger.LogInformation("--- FINAL RECORDING SCHEDULE ({Count} recordings) ---", schedule.Count);
        foreach (var rec in schedule.OrderBy(r => r.StartTime))
        {
            _logger.LogInformation(
                "  {Start} - {End} | {Title} | priority #{SortOrder} ({SubName})",
                rec.StartTime.ToLocalTime().ToString("g"),
                rec.EndTime.ToLocalTime().ToString("g"),
                rec.Title,
                rec.Match.Subscription.SortOrder + 1,
                rec.Match.Subscription.Name);
        }

        var skippedCount = uniqueGames.Count - schedule.Count;
        if (skippedCount > 0)
        {
            _logger.LogInformation("  ({Skipped} games could not fit due to concurrent stream limits)", skippedCount);
        }

        return schedule;
    }

    /// <summary>
    /// Builds the schedule tier by tier.
    /// For each tier, uses interval scheduling to maximize the number of recordings
    /// that fit into the remaining available slots.
    /// </summary>
    private List<ScheduledRecording> BuildScheduleByTiers(
        List<PriorityTier> tiers,
        int maxConcurrent,
        List<(DateTime Start, DateTime End)>? existingSlots)
    {
        var schedule = new List<ScheduledRecording>();

        // Committed time slots — starts with any existing timers from previous scans
        var committedSlots = existingSlots?.ToList() ?? new List<(DateTime Start, DateTime End)>();

        foreach (var tier in tiers)
        {
            // Sort this tier's games by end time (earliest-ending first).
            // This is the classic interval scheduling greedy algorithm:
            // by always picking the game that ends soonest, we leave the most
            // room for subsequent games. This maximizes total recordings.
            var candidates = tier.Games
                .OrderBy(g => g.Primary.Program.EndDate)
                .ThenBy(g => g.Primary.Program.StartDate)
                .ToList();

            var scheduledFromTier = 0;
            var skippedFromTier = 0;

            foreach (var game in candidates)
            {
                var program = game.Primary.Program;
                var startTime = program.StartDate;
                var endTime = program.EndDate;

                // Count how many committed slots overlap this game's window
                var overlapping = committedSlots
                    .Count(s => s.Start < endTime && s.End > startTime);

                if (overlapping < maxConcurrent)
                {
                    // Slot available — schedule it
                    var rec = CreateRecording(game);
                    schedule.Add(rec);
                    committedSlots.Add((startTime, endTime));
                    scheduledFromTier++;

                    _logger.LogDebug(
                        "  Tier #{Sort} scheduled: '{Title}' {Start}-{End} ({Concurrent}/{Max} concurrent)",
                        tier.SortOrder + 1,
                        program.Name,
                        startTime.ToLocalTime().ToString("g"),
                        endTime.ToLocalTime().ToString("g"),
                        overlapping + 1,
                        maxConcurrent);
                }
                else
                {
                    skippedFromTier++;
                }
            }

            if (scheduledFromTier > 0 || skippedFromTier > 0)
            {
                _logger.LogInformation(
                    "Tier #{Sort} ({SubName}): scheduled {Scheduled} of {Total} games{Skipped}",
                    tier.SortOrder + 1,
                    tier.SubscriptionName,
                    scheduledFromTier,
                    candidates.Count,
                    skippedFromTier > 0 ? $" ({skippedFromTier} couldn't fit)" : "");
            }
        }

        return schedule;
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
/// A priority tier — all games from the same subscription.
/// </summary>
internal class PriorityTier
{
    public int SortOrder { get; set; }
    public string SubscriptionName { get; set; } = "";
    public SubscriptionType SubscriptionType { get; set; }
    public List<GameGroup> Games { get; set; } = new();
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
