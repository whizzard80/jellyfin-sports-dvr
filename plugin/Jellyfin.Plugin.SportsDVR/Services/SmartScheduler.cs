using System;
using System.Collections.Generic;
using System.Linq;
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

                // Pick best channel (prefer sports channels, then by subscription priority)
                var best = allOptions
                    .OrderByDescending(m => m.Scored.IsSportsChannel ? 1 : 0)
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
    /// </summary>
    private bool IsSameGame(MatchedProgram a, MatchedProgram b)
    {
        // Different times = different games
        var timeDiff = Math.Abs((a.Program.StartDate - b.Program.StartDate).TotalMinutes);
        if (timeDiff > 30) // Allow 30 min tolerance for different broadcast schedules
        {
            return false;
        }

        // Check if same teams
        var teamsA = GetNormalizedTeams(a);
        var teamsB = GetNormalizedTeams(b);

        if (teamsA.Count == 2 && teamsB.Count == 2)
        {
            // Both have two teams - check if same matchup
            return teamsA.SetEquals(teamsB);
        }

        // Fall back to title similarity
        var titleA = NormalizeTitle(a.Program.Name ?? "");
        var titleB = NormalizeTitle(b.Program.Name ?? "");

        // Check for high similarity (same core matchup)
        return CalculateSimilarity(titleA, titleB) > 0.7;
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
    /// </summary>
    private List<ScheduledRecording> BuildSchedule(List<GameGroup> sortedGames, int maxConcurrent)
    {
        var schedule = new List<ScheduledRecording>();
        var activeRecordings = new List<ScheduledRecording>();

        foreach (var game in sortedGames)
        {
            var program = game.Primary.Program;
            var startTime = program.StartDate;
            var endTime = program.EndDate;

            // Remove finished recordings from active list
            activeRecordings.RemoveAll(r => r.EndTime <= startTime);

            // Check if we have capacity
            if (activeRecordings.Count >= maxConcurrent)
            {
                // No capacity - check if we should preempt a lower priority recording
                var lowestPriority = activeRecordings
                    .Where(r => r.StartTime < endTime && r.EndTime > startTime) // Overlapping
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
                    activeRecordings.Remove(lowestPriority);
                }
                else
                {
                    // Can't schedule - log conflict
                    _logger.LogWarning(
                        "Cannot schedule '{Game}' - no capacity (max {Max} concurrent)",
                        program.Name,
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
            activeRecordings.Add(recording);

            _logger.LogDebug(
                "Scheduled: {Title} at {Time} on {Channel} (priority {Priority})",
                recording.Title,
                recording.StartTime.ToLocalTime(),
                recording.ChannelName,
                recording.Priority);
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
