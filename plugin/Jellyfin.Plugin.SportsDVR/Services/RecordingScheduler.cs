using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SportsDVR.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsDVR.Services;

/// <summary>
/// Background service that scans EPG and schedules recordings via Jellyfin's DVR.
/// This decides WHAT to record; Jellyfin's DVR handles the actual recording.
/// Uses SmartScheduler for optimized scheduling with connection limits.
/// </summary>
public class RecordingScheduler : IHostedService, IDisposable
{
    private readonly ILogger<RecordingScheduler> _logger;
    private readonly ILiveTvManager _liveTvManager;
    private readonly SportsScorer _sportsScorer;
    private readonly AliasService _aliasService;
    private readonly SubscriptionManager _subscriptionManager;
    private readonly SmartScheduler _smartScheduler;
    
    private Timer? _scanTimer;
    private readonly HashSet<string> _scheduledPrograms = new();
    private bool _disposed;

    // Default lookahead is 3 days
    private const int DEFAULT_LOOKAHEAD_DAYS = 3;
    private const int DEFAULT_MAX_CONCURRENT = 2;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingScheduler"/> class.
    /// </summary>
    public RecordingScheduler(
        ILogger<RecordingScheduler> logger,
        ILiveTvManager liveTvManager,
        SportsScorer sportsScorer,
        AliasService aliasService,
        SubscriptionManager subscriptionManager,
        SmartScheduler smartScheduler)
    {
        _logger = logger;
        _liveTvManager = liveTvManager;
        _sportsScorer = sportsScorer;
        _aliasService = aliasService;
        _subscriptionManager = subscriptionManager;
        _smartScheduler = smartScheduler;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Sports DVR Recording Scheduler starting");

        var config = Plugin.Instance?.Configuration;
        var intervalMinutes = config?.ScanIntervalMinutes ?? 5;

        // Start timer to periodically scan EPG
        _scanTimer = new Timer(
            DoScan,
            null,
            TimeSpan.FromSeconds(30), // Initial delay to let Jellyfin fully start
            TimeSpan.FromMinutes(intervalMinutes));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Sports DVR Recording Scheduler stopping");
        _scanTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _scanTimer?.Dispose();
        }

        _disposed = true;
    }

    private async void DoScan(object? state)
    {
        try
        {
            await ScanEpgAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during EPG scan");
        }
    }

    /// <summary>
    /// Scans the EPG for programs matching subscriptions and creates an optimized schedule.
    /// </summary>
    public async Task ScanEpgAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnableAutoScheduling)
        {
            _logger.LogDebug("Auto-scheduling disabled, skipping scan");
            return;
        }

        var subscriptions = _subscriptionManager.GetEnabled();
        if (subscriptions.Count == 0)
        {
            _logger.LogDebug("No enabled subscriptions, skipping scan");
            return;
        }

        var maxConcurrent = config.MaxConcurrentRecordings > 0 
            ? config.MaxConcurrentRecordings 
            : DEFAULT_MAX_CONCURRENT;

        _logger.LogInformation(
            "Scanning EPG for {Count} subscriptions (max {Max} concurrent recordings, {Days} day lookahead)",
            subscriptions.Count,
            maxConcurrent,
            DEFAULT_LOOKAHEAD_DAYS);

        try
        {
            // Step 1: Get all upcoming programs (3 day lookahead)
            var programs = await GetUpcomingProgramsAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Found {Count} programs in EPG", programs.Count);

            if (programs.Count == 0)
            {
                return;
            }

            // Step 2: Get existing timers to avoid duplicates
            var existingTimers = await GetExistingTimersAsync(cancellationToken).ConfigureAwait(false);
            var existingProgramIds = new HashSet<string>(
                existingTimers
                    .Select(t => t.ProgramId)
                    .Where(id => !string.IsNullOrEmpty(id))!);

            // Step 3: Find all matching programs
            var matches = new List<MatchedProgram>();

            foreach (var program in programs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip if already scheduled
                if (existingProgramIds.Contains(program.Id) || _scheduledPrograms.Contains(program.Id))
                {
                    continue;
                }

                // Score the program
                var scored = _sportsScorer.Score(
                    program.Name ?? string.Empty,
                    program.ChannelName,
                    program.Overview,
                    program.IsSports);

                // Must be a likely sports event
                if (scored.Score < SportsScorer.THRESHOLD_LIKELY_GAME)
                {
                    continue;
                }

                // Find matching subscription
                var matchedSub = FindMatchingSubscription(program, scored, subscriptions);
                if (matchedSub == null)
                {
                    continue;
                }

                // Check exclusion patterns
                if (ShouldExclude(program, scored, matchedSub))
                {
                    _logger.LogDebug("Excluding '{Title}' - exclusion pattern or replay", program.Name);
                    continue;
                }

                matches.Add(new MatchedProgram
                {
                    Program = program,
                    Scored = scored,
                    Subscription = matchedSub
                });
            }

            _logger.LogInformation("Found {Count} programs matching subscriptions", matches.Count);

            if (matches.Count == 0)
            {
                return;
            }

            // Step 4: Use SmartScheduler to create optimized schedule
            var schedule = _smartScheduler.CreateSchedule(matches, maxConcurrent);

            // Step 5: Create timers for scheduled recordings
            var scheduledCount = 0;
            foreach (var recording in schedule)
            {
                try
                {
                    await ScheduleRecordingAsync(recording, cancellationToken).ConfigureAwait(false);
                    scheduledCount++;
                    _scheduledPrograms.Add(recording.Program.Id);

                    var backupInfo = recording.HasBackupChannels ? " (backup channels available)" : "";
                    _logger.LogInformation(
                        "✅ Scheduled: {Title} at {Time} on {Channel} (priority {Pri}){Backup}",
                        recording.Title,
                        recording.StartTime.ToLocalTime().ToString("g"),
                        recording.ChannelName,
                        recording.Priority,
                        backupInfo);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to schedule recording for '{Title}'", recording.Title);
                }
            }

            _logger.LogInformation(
                "📺 EPG scan complete: {Matches} matches → {Scheduled} recordings scheduled",
                matches.Count,
                scheduledCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during EPG scan");
        }
    }

    private async Task<List<ProgramInfo>> GetUpcomingProgramsAsync(CancellationToken cancellationToken)
    {
        var result = new List<ProgramInfo>();
        var endDate = DateTime.UtcNow.AddDays(DEFAULT_LOOKAHEAD_DAYS);

        try
        {
            // Get all Live TV channels
            var channelQuery = new LiveTvChannelQuery { };
            var dtoOptions = new DtoOptions();
            var channels = _liveTvManager.GetInternalChannels(channelQuery, dtoOptions, cancellationToken);

            _logger.LogDebug("Scanning {Count} Live TV channels", channels.Items.Count);

            foreach (var channel in channels.Items)
            {
                try
                {
                    // Get program guide for this channel
                    var programQuery = new InternalItemsQuery
                    {
                        ChannelIds = new[] { channel.Id },
                        MinStartDate = DateTime.UtcNow,
                        MaxStartDate = endDate
                    };

                    var guide = await _liveTvManager.GetPrograms(programQuery, dtoOptions, cancellationToken).ConfigureAwait(false);

                    // Get service name from channel
                    var serviceName = channel.ServiceName ?? string.Empty;
                    
                    foreach (var program in guide.Items)
                    {
                        var startDate = program.StartDate ?? DateTime.UtcNow;
                        result.Add(new ProgramInfo
                        {
                            Id = program.Id.ToString(),
                            ProgramGuid = program.Id,
                            Name = program.Name,
                            Overview = program.Overview,
                            ChannelGuid = channel.Id,
                            ChannelId = channel.Id.ToString(),
                            ChannelName = channel.Name,
                            ServiceName = serviceName,
                            StartDate = startDate,
                            EndDate = program.EndDate ?? startDate.AddHours(3),
                            IsSports = program.IsSports ?? false,
                            IsLive = program.IsLive ?? false,
                            IsNew = program.IsNews ?? false,
                            IsPremiere = program.IsPremiere ?? false,
                            IsRepeat = program.IsRepeat ?? false
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get programs for channel {Channel}", channel.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query Live TV programs");
        }

        return result;
    }

    private async Task<IReadOnlyList<TimerInfoDto>> GetExistingTimersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var timers = await _liveTvManager.GetTimers(
                new TimerQuery(),
                cancellationToken).ConfigureAwait(false);
            return timers.Items;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get existing timers");
            return Array.Empty<TimerInfoDto>();
        }
    }

    private Subscription? FindMatchingSubscription(
        ProgramInfo program,
        ScoredProgram scored,
        IReadOnlyList<Subscription> subscriptions)
    {
        var title = program.Name?.ToLowerInvariant() ?? string.Empty;
        var description = program.Overview?.ToLowerInvariant() ?? string.Empty;
        var combinedText = $"{title} {description}";

        foreach (var sub in subscriptions.OrderByDescending(s => s.Priority))
        {
            var pattern = sub.MatchPattern?.ToLowerInvariant() ?? sub.Name.ToLowerInvariant();
            bool isMatch = false;

            switch (sub.Type)
            {
                case SubscriptionType.Team:
                    // Resolve alias for the subscription pattern (also lowercase for comparison)
                    var resolvedPattern = _aliasService.ResolveAlias(pattern).ToLowerInvariant();
                    
                    // Check if pattern appears in title/description (case-insensitive)
                    if (combinedText.Contains(resolvedPattern, StringComparison.OrdinalIgnoreCase) ||
                        combinedText.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        isMatch = true;
                        _logger.LogDebug("Matched '{Title}' to subscription '{Sub}' via pattern '{Pattern}'",
                            program.Name, sub.Name, pattern);
                    }
                    // Also check against extracted team names
                    else if (!string.IsNullOrEmpty(scored.Team1) || !string.IsNullOrEmpty(scored.Team2))
                    {
                        var team1 = _aliasService.ResolveAlias(scored.Team1?.ToLowerInvariant() ?? string.Empty).ToLowerInvariant();
                        var team2 = _aliasService.ResolveAlias(scored.Team2?.ToLowerInvariant() ?? string.Empty).ToLowerInvariant();
                        
                        if (team1.Contains(resolvedPattern, StringComparison.OrdinalIgnoreCase) || 
                            team2.Contains(resolvedPattern, StringComparison.OrdinalIgnoreCase) ||
                            resolvedPattern.Contains(team1, StringComparison.OrdinalIgnoreCase) || 
                            resolvedPattern.Contains(team2, StringComparison.OrdinalIgnoreCase) ||
                            team1.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                            team2.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        {
                            isMatch = true;
                            _logger.LogDebug("Matched '{Title}' to subscription '{Sub}' via team extraction",
                                program.Name, sub.Name);
                        }
                    }
                    break;

                case SubscriptionType.League:
                    if (IsRegexPattern(pattern))
                    {
                        isMatch = MatchesRegex(combinedText, pattern);
                    }
                    else
                    {
                        isMatch = combinedText.Contains(pattern);
                    }
                    break;

                case SubscriptionType.Event:
                    if (IsRegexPattern(pattern))
                    {
                        isMatch = MatchesRegex(combinedText, pattern);
                    }
                    else
                    {
                        isMatch = combinedText.Contains(pattern);
                    }
                    break;
            }

            if (isMatch)
            {
                return sub;
            }
        }

        return null;
    }

    private bool ShouldExclude(ProgramInfo program, ScoredProgram scored, Subscription subscription)
    {
        var title = program.Name?.ToLowerInvariant() ?? string.Empty;
        var description = program.Overview?.ToLowerInvariant() ?? string.Empty;
        var combinedText = $"{title} {description}";

        // Check exclusion patterns
        if (subscription.ExcludePatterns != null)
        {
            foreach (var excludePattern in subscription.ExcludePatterns)
            {
                var pattern = excludePattern.ToLowerInvariant();
                if (IsRegexPattern(pattern))
                {
                    if (MatchesRegex(combinedText, pattern))
                    {
                        return true;
                    }
                }
                else if (combinedText.Contains(pattern))
                {
                    return true;
                }
            }
        }

        // Don't record replays unless explicitly allowed
        if (!subscription.IncludeReplays)
        {
            if (program.IsRepeat || scored.IsReplay)
            {
                return true;
            }
        }

        // Skip pre/post game shows
        if (scored.IsPregamePostgame)
        {
            return true;
        }

        return false;
    }

    private async Task ScheduleRecordingAsync(
        ScheduledRecording recording,
        CancellationToken cancellationToken)
    {
        var program = recording.Program;
        var subscription = recording.Match.Subscription;
        var scored = recording.Match.Scored;

        // Get default timer settings from Jellyfin DVR
        var defaults = await _liveTvManager.GetNewTimerDefaults(cancellationToken).ConfigureAwait(false);

        // Create timer info - Jellyfin's DVR will handle the actual recording
        var timerInfo = new TimerInfoDto
        {
            Name = program.Name,
            Overview = BuildRecordingDescription(recording),
            ChannelId = program.ChannelGuid,
            ProgramId = program.Id,
            StartDate = program.StartDate,
            EndDate = program.EndDate,
            ServiceName = program.ServiceName, // Required for Jellyfin to know which tuner to use
            
            // Use defaults but add extra padding for sports (games often run over)
            PrePaddingSeconds = defaults.PrePaddingSeconds,
            PostPaddingSeconds = Math.Max(defaults.PostPaddingSeconds, 1800), // At least 30 min padding
            
            IsPrePaddingRequired = defaults.IsPrePaddingRequired,
            IsPostPaddingRequired = true, // Sports often run over
            
            Priority = subscription.Priority
        };

        // Create the timer via Jellyfin's Live TV manager
        await _liveTvManager.CreateTimer(timerInfo, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildRecordingDescription(ScheduledRecording recording)
    {
        var program = recording.Program;
        var scored = recording.Match.Scored;
        var subscription = recording.Match.Subscription;
        var parts = new List<string>();
        
        if (!string.IsNullOrEmpty(scored.DetectedLeague))
        {
            parts.Add($"League: {scored.DetectedLeague}");
        }
        
        if (!string.IsNullOrEmpty(scored.Team1) && !string.IsNullOrEmpty(scored.Team2))
        {
            parts.Add($"Matchup: {scored.Team1} vs {scored.Team2}");
        }
        
        parts.Add($"Subscription: {subscription.Name} ({subscription.Type})");
        parts.Add($"Priority: {recording.Priority}");

        if (recording.HasBackupChannels)
        {
            parts.Add($"Backup channels available");
        }

        if (!string.IsNullOrEmpty(program.Overview))
        {
            parts.Add(program.Overview);
        }

        return string.Join(" | ", parts);
    }

    private static bool IsRegexPattern(string pattern)
    {
        return pattern.StartsWith("/") && pattern.Length > 2 && 
               (pattern.EndsWith("/") || pattern.EndsWith("/i"));
    }

    private static bool MatchesRegex(string text, string pattern)
    {
        try
        {
            var caseInsensitive = pattern.EndsWith("/i");
            var regexPattern = pattern.TrimStart('/');
            if (caseInsensitive)
            {
                regexPattern = regexPattern[..^2];
            }
            else
            {
                regexPattern = regexPattern.TrimEnd('/');
            }

            var options = caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None;
            return Regex.IsMatch(text, regexPattern, options);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Internal program info for EPG scanning.
/// </summary>
public class ProgramInfo
{
    /// <summary>Gets or sets the program ID.</summary>
    public string Id { get; set; } = string.Empty;
    
    /// <summary>Gets or sets the program GUID.</summary>
    public Guid ProgramGuid { get; set; }
    
    /// <summary>Gets or sets the program name.</summary>
    public string? Name { get; set; }
    
    /// <summary>Gets or sets the program description.</summary>
    public string? Overview { get; set; }
    
    /// <summary>Gets or sets the channel ID as GUID.</summary>
    public Guid ChannelGuid { get; set; }
    
    /// <summary>Gets or sets the channel ID as string.</summary>
    public string ChannelId { get; set; } = string.Empty;
    
    /// <summary>Gets or sets the channel name.</summary>
    public string? ChannelName { get; set; }
    
    /// <summary>Gets or sets the Live TV service name for recording.</summary>
    public string ServiceName { get; set; } = string.Empty;
    
    /// <summary>Gets or sets the start time.</summary>
    public DateTime StartDate { get; set; }
    
    /// <summary>Gets or sets the end time.</summary>
    public DateTime EndDate { get; set; }
    
    /// <summary>Gets or sets whether this is a sports program.</summary>
    public bool IsSports { get; set; }
    
    /// <summary>Gets or sets whether this is live.</summary>
    public bool IsLive { get; set; }
    
    /// <summary>Gets or sets whether this is new.</summary>
    public bool IsNew { get; set; }
    
    /// <summary>Gets or sets whether this is a premiere.</summary>
    public bool IsPremiere { get; set; }
    
    /// <summary>Gets or sets whether this is a repeat.</summary>
    public bool IsRepeat { get; set; }
}
