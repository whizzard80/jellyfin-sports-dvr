using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.SportsDVR.Configuration;
using Jellyfin.Plugin.SportsDVR.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsDVR.Services;

/// <summary>
/// Background service that scans EPG and schedules recordings via Jellyfin's DVR.
/// This decides WHAT to record; Jellyfin's DVR handles the actual recording.
/// Uses SmartScheduler for optimized scheduling with connection limits.
/// </summary>
public class RecordingScheduler
{
    private readonly ILogger<RecordingScheduler> _logger;
    private readonly ILiveTvManager _liveTvManager;
    private readonly SportsScorer _sportsScorer;
    private readonly AliasService _aliasService;
    private readonly SubscriptionManager _subscriptionManager;
    private readonly SmartScheduler _smartScheduler;
    private readonly GuideCachePurgeService _guideCachePurgeService;
    
    private readonly HashSet<string> _scheduledPrograms = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    // Teamarr regenerates daily; 24-hour lookahead is the right window.
    private const int DEFAULT_LOOKAHEAD_HOURS = 24;
    private const int DEFAULT_MAX_CONCURRENT = 2;
    
    /// <summary>
    /// Tracks whether the last guide cache purge has been followed by a full rebuild.
    /// </summary>
    private DateTime? _lastFullRebuildAfterPurge;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingScheduler"/> class.
    /// </summary>
    public RecordingScheduler(
        ILogger<RecordingScheduler> logger,
        ILiveTvManager liveTvManager,
        SportsScorer sportsScorer,
        AliasService aliasService,
        SubscriptionManager subscriptionManager,
        SmartScheduler smartScheduler,
        GuideCachePurgeService guideCachePurgeService)
    {
        _logger = logger;
        _liveTvManager = liveTvManager;
        _sportsScorer = sportsScorer;
        _aliasService = aliasService;
        _subscriptionManager = subscriptionManager;
        _smartScheduler = smartScheduler;
        _guideCachePurgeService = guideCachePurgeService;
    }

    /// <summary>
    /// Clears the internal cache of scheduled program IDs.
    /// Use this when the tuner or EPG source changes to force re-evaluation of all programs.
    /// </summary>
    public void ClearScheduledProgramsCache()
    {
        _logger.LogInformation("Clearing scheduled programs cache ({Count} entries)", _scheduledPrograms.Count);
        _scheduledPrograms.Clear();
    }

    /// <summary>
    /// Results from an EPG scan.
    /// </summary>
    public class ScanResult
    {
        /// <summary>Gets or sets the number of programs matching subscriptions.</summary>
        public int MatchesFound { get; set; }

        /// <summary>Gets or sets the number of new recordings scheduled.</summary>
        public int NewRecordings { get; set; }

        /// <summary>Gets or sets the number of recordings that already existed.</summary>
        public int AlreadyScheduled { get; set; }
    }

    /// <summary>
    /// Cancels all Jellyfin DVR timers that were created by Sports DVR subscriptions.
    /// Identifies our timers by checking if the timer's program title matches any active subscription.
    /// </summary>
    public async Task<int> CancelSportsDvrTimersAsync(CancellationToken cancellationToken)
    {
        var timers = await GetExistingTimersAsync(cancellationToken).ConfigureAwait(false);
        var subscriptions = _subscriptionManager.GetAll();
        var cancelledCount = 0;

        foreach (var timer in timers)
        {
            // Check if this timer matches any of our subscriptions
            var titleLower = timer.Name?.ToLowerInvariant() ?? string.Empty;
            var overviewLower = timer.Overview?.ToLowerInvariant() ?? string.Empty;

            var isOurs = false;

            // Check by overview containing "Subscription:" (our signature in BuildRecordingDescription)
            if (overviewLower.Contains("subscription:"))
            {
                isOurs = true;
            }
            else
            {
                // Fallback: check if title matches any subscription pattern
                foreach (var sub in subscriptions)
                {
                    var pattern = sub.MatchPattern?.ToLowerInvariant() ?? sub.Name.ToLowerInvariant();
                    if (titleLower.Contains(pattern) || overviewLower.Contains(pattern))
                    {
                        isOurs = true;
                        break;
                    }
                }
            }

            if (isOurs)
            {
                try
                {
                    await _liveTvManager.CancelTimer(timer.Id).ConfigureAwait(false);
                    cancelledCount++;
                    _logger.LogInformation("Cancelled timer: {Title} at {Time}", timer.Name, timer.StartDate.ToLocalTime().ToString("g"));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cancel timer '{Title}'", timer.Name);
                }
            }
        }

        // Clear our internal cache too
        _scheduledPrograms.Clear();
        _logger.LogInformation("Cancelled {Count} Sports DVR timers", cancelledCount);

        return cancelledCount;
    }

    /// <summary>
    /// Cancels ALL scheduled timers from Jellyfin's DVR — not just Sports DVR ones.
    /// Use this as a nuclear option to clean up orphaned timers from old plugin versions.
    /// </summary>
    public async Task<int> CancelAllTimersAsync(CancellationToken cancellationToken)
    {
        var timers = await GetExistingTimersAsync(cancellationToken).ConfigureAwait(false);
        var cancelledCount = 0;
        var failedCount = 0;

        _logger.LogWarning("NUCLEAR: Cancelling ALL {Count} scheduled timers", timers.Count);

        foreach (var timer in timers)
        {
            try
            {
                await _liveTvManager.CancelTimer(timer.Id).ConfigureAwait(false);
                cancelledCount++;
                _logger.LogInformation("Cancelled timer: {Title} at {Time} (channel: {Channel})",
                    timer.Name,
                    timer.StartDate.ToLocalTime().ToString("g"),
                    timer.ChannelName ?? "unknown");
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogWarning(ex, "Failed to cancel timer '{Title}'", timer.Name);
            }
        }

        // Clear our internal cache too
        _scheduledPrograms.Clear();
        _logger.LogInformation("NUCLEAR complete: cancelled {Cancelled}, failed {Failed} out of {Total} total timers",
            cancelledCount, failedCount, timers.Count);

        return cancelledCount;
    }

    /// <summary>
    /// Scans the EPG for programs matching subscriptions and creates an optimized schedule.
    /// Supports two modes:
    /// - Full scan (after guide cache purge): wipe all Sports DVR timers and rebuild from scratch.
    /// - Confirmation scan (normal): only add new matches and remove stale timers.
    /// </summary>
    public async Task<ScanResult> ScanEpgAsync(CancellationToken cancellationToken)
    {
        var result = new ScanResult();
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnableAutoScheduling)
        {
            _logger.LogDebug("Auto-scheduling disabled, skipping scan");
            return result;
        }

        // Prevent concurrent scans (scheduled task + manual trigger could overlap)
        if (!await _scanLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("EPG scan already in progress, skipping");
            return result;
        }

        try
        {
            var subscriptions = _subscriptionManager.GetEnabled();
            if (subscriptions.Count == 0)
            {
                _logger.LogDebug("No enabled subscriptions, skipping scan");
                return result;
            }

            // Respect plugin setting; clamp to valid range so we never exceed tuner capacity.
            var maxConcurrent = config.MaxConcurrentRecordings > 0
                ? Math.Min(Math.Max(config.MaxConcurrentRecordings, 1), 20)
                : DEFAULT_MAX_CONCURRENT;

            // Determine scan mode: full rebuild vs confirmation
            var isFullScan = ShouldDoFullScan();

            _logger.LogInformation(
                "{Mode} EPG scan for {Count} subscriptions (max {Max} concurrent, {Hours}h lookahead)",
                isFullScan ? "FULL REBUILD" : "CONFIRMATION",
                subscriptions.Count,
                maxConcurrent,
                DEFAULT_LOOKAHEAD_HOURS);

            if (isFullScan)
            {
                result = await FullScanAsync(subscriptions, maxConcurrent, cancellationToken).ConfigureAwait(false);
                _lastFullRebuildAfterPurge = DateTime.UtcNow;
            }
            else
            {
                result = await ConfirmationScanAsync(subscriptions, maxConcurrent, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during EPG scan");
        }
        finally
        {
            _scanLock.Release();
        }

        return result;
    }

    /// <summary>
    /// Determines whether we should do a full rebuild or a confirmation scan.
    /// Full rebuild happens after a guide cache purge that hasn't been followed by a rebuild yet.
    /// </summary>
    private bool ShouldDoFullScan()
    {
        var lastPurge = _guideCachePurgeService.GetLastPurgeTime();
        if (lastPurge == null)
        {
            return false; // No purge has happened; confirmation scan
        }

        // If we haven't done a full rebuild since the last purge, do one now
        if (_lastFullRebuildAfterPurge == null || _lastFullRebuildAfterPurge < lastPurge)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Full scan: wipe all Sports DVR timers, rebuild schedule from scratch.
    /// Used after a guide cache purge when we have fresh EPG data.
    /// </summary>
    private async Task<ScanResult> FullScanAsync(
        IReadOnlyList<Subscription> subscriptions,
        int maxConcurrent,
        CancellationToken cancellationToken)
    {
        var result = new ScanResult();

        // Step 1: Cancel all existing Sports DVR timers (clean slate)
        var cancelled = await CancelSportsDvrTimersAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Full scan: wiped {Count} existing Sports DVR timers", cancelled);

        // Step 2: Load today's EPG
        var programs = await GetUpcomingProgramsAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Found {Count} programs in EPG ({Hours}h lookahead)", programs.Count, DEFAULT_LOOKAHEAD_HOURS);

        if (programs.Count == 0)
        {
            return result;
        }

        // Step 3: Find all matching programs
        var matches = FindAllMatches(programs, subscriptions, cancellationToken);
        result.MatchesFound = matches.Count;

        _logger.LogInformation("Found {Count} programs matching subscriptions", matches.Count);

        if (matches.Count == 0)
        {
            return result;
        }

        // Step 4: Build optimized schedule
        var schedule = _smartScheduler.CreateSchedule(matches, maxConcurrent);

        // Step 5: Create timers
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
                    "Scheduled: {Title} at {Time} on {Channel} (priority {Pri}){Backup}",
                    recording.Title,
                    recording.StartTime.ToLocalTime().ToString("g"),
                    recording.ChannelName,
                    recording.Priority,
                    backupInfo);
            }
            catch (ArgumentException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // Shouldn't happen after a wipe, but handle gracefully
                _scheduledPrograms.Add(recording.Program.Id);
                _logger.LogDebug("Timer already exists for '{Title}' - skipping", recording.Title);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to schedule recording for '{Title}'", recording.Title);
            }
        }

        result.NewRecordings = scheduledCount;

        _logger.LogInformation(
            "FULL SCAN complete: {Matches} matches, {Scheduled} recordings created (wiped {Cancelled} old timers)",
            matches.Count,
            scheduledCount,
            cancelled);

        return result;
    }

    /// <summary>
    /// Confirmation scan: compare current EPG matches against existing timers.
    /// Adds new matches and removes stale timers.
    /// </summary>
    private async Task<ScanResult> ConfirmationScanAsync(
        IReadOnlyList<Subscription> subscriptions,
        int maxConcurrent,
        CancellationToken cancellationToken)
    {
        var result = new ScanResult();

        // Step 1: Load EPG and existing timers
        var programs = await GetUpcomingProgramsAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Found {Count} programs in EPG ({Hours}h lookahead)", programs.Count, DEFAULT_LOOKAHEAD_HOURS);

        if (programs.Count == 0)
        {
            return result;
        }

        var existingTimers = await GetExistingTimersAsync(cancellationToken).ConfigureAwait(false);
        var existingProgramIds = new HashSet<string>(
            existingTimers
                .Select(t => t.ProgramId)
                .Where(id => !string.IsNullOrEmpty(id))!);
        var existingTimerKeys = new HashSet<string>(
            existingTimers.Select(t => BuildTimerKey(t.Name, t.ChannelId.ToString(), t.StartDate)),
            StringComparer.OrdinalIgnoreCase);

        // Step 2: Find all matching programs
        var allMatches = FindAllMatches(programs, subscriptions, cancellationToken);
        result.MatchesFound = allMatches.Count;

        // Step 3: Separate into new vs already-scheduled
        var newMatches = new List<MatchedProgram>();
        var alreadyScheduledCount = 0;

        foreach (var match in allMatches)
        {
            var timerKey = BuildTimerKey(match.Program.Name, match.Program.ChannelGuid.ToString(), match.Program.StartDate);
            if (existingProgramIds.Contains(match.Program.Id)
                || _scheduledPrograms.Contains(match.Program.Id)
                || existingTimerKeys.Contains(timerKey))
            {
                alreadyScheduledCount++;
            }
            else
            {
                newMatches.Add(match);
            }
        }

        _logger.LogInformation(
            "Confirmation scan: {Total} matches ({New} new, {Existing} already scheduled)",
            allMatches.Count, newMatches.Count, alreadyScheduledCount);

        // Step 4: Schedule new matches if slots available
        var scheduledCount = 0;
        var alreadyExisted = 0;
        if (newMatches.Count > 0)
        {
            var schedule = _smartScheduler.CreateSchedule(newMatches, maxConcurrent);
            foreach (var recording in schedule)
            {
                try
                {
                    await ScheduleRecordingAsync(recording, cancellationToken).ConfigureAwait(false);
                    scheduledCount++;
                    _scheduledPrograms.Add(recording.Program.Id);

                    _logger.LogInformation(
                        "Scheduled (new): {Title} at {Time} on {Channel}",
                        recording.Title,
                        recording.StartTime.ToLocalTime().ToString("g"),
                        recording.ChannelName);
                }
                catch (ArgumentException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                {
                    alreadyExisted++;
                    _scheduledPrograms.Add(recording.Program.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to schedule recording for '{Title}'", recording.Title);
                }
            }
        }

        // Step 5: Check for stale timers (programs removed from EPG)
        var removedCount = 0;
        var now = DateTime.UtcNow;
        var matchedProgramKeys = new HashSet<string>(
            allMatches.Select(m => BuildTimerKey(m.Program.Name, m.Program.ChannelGuid.ToString(), m.Program.StartDate)),
            StringComparer.OrdinalIgnoreCase);

        // Build a set of program IDs currently in the EPG (not from timers)
        // so we can detect when a timer's program has been removed from the guide
        var currentEpgProgramIds = new HashSet<string>(
            programs.Select(p => p.Id).Where(id => !string.IsNullOrEmpty(id)));

        foreach (var timer in existingTimers)
        {
            // Only check future timers that we created (identified by "Subscription:" in overview)
            if (timer.StartDate < now) continue;
            var overview = timer.Overview?.ToLowerInvariant() ?? string.Empty;
            if (!overview.Contains("subscription:")) continue;

            var timerKey = BuildTimerKey(timer.Name, timer.ChannelId.ToString(), timer.StartDate);
            if (!matchedProgramKeys.Contains(timerKey) && !currentEpgProgramIds.Contains(timer.ProgramId ?? string.Empty))
            {
                // This timer no longer matches any EPG program - may have been removed
                _logger.LogWarning(
                    "Stale timer detected: '{Title}' at {Time} - program no longer in EPG, cancelling",
                    timer.Name,
                    timer.StartDate.ToLocalTime().ToString("g"));

                try
                {
                    await _liveTvManager.CancelTimer(timer.Id).ConfigureAwait(false);
                    removedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cancel stale timer '{Title}'", timer.Name);
                }
            }
        }

        result.NewRecordings = scheduledCount;
        result.AlreadyScheduled = alreadyScheduledCount + alreadyExisted;

        _logger.LogInformation(
            "CONFIRMATION scan complete: {New} additions, {Removed} removals, {Existing} confirmed",
            scheduledCount,
            removedCount,
            alreadyScheduledCount);

        return result;
    }

    /// <summary>
    /// Finds all programs matching subscriptions (scoring, matching, filtering).
    /// </summary>
    private List<MatchedProgram> FindAllMatches(
        List<ProgramInfo> programs,
        IReadOnlyList<Subscription> subscriptions,
        CancellationToken cancellationToken)
    {
        var matches = new List<MatchedProgram>();

        foreach (var program in programs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Score the program - include EpisodeTitle in description for raw EPG
            var descWithEpisode = string.Join(" ", new[] { 
                program.EpisodeTitle, 
                program.Overview 
            }.Where(s => !string.IsNullOrEmpty(s)));
            
            var scored = _sportsScorer.Score(
                program.Name ?? string.Empty,
                program.ChannelName,
                descWithEpisode,
                program.IsSports);

            if (scored.Score < SportsScorer.THRESHOLD_LIKELY_GAME)
            {
                continue;
            }

            var matchedSub = FindMatchingSubscription(program, scored, subscriptions);
            if (matchedSub == null)
            {
                continue;
            }

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

        return matches;
    }

    private async Task<List<ProgramInfo>> GetUpcomingProgramsAsync(CancellationToken cancellationToken)
    {
        var result = new List<ProgramInfo>();
        var endDate = DateTime.UtcNow.AddHours(DEFAULT_LOOKAHEAD_HOURS);

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
                            IsRepeat = program.IsRepeat ?? false,
                            EpisodeTitle = program.EpisodeTitle  // Teamarr stores league info here (e.g., "NBA")
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
        var episodeTitle = program.EpisodeTitle?.ToLowerInvariant() ?? string.Empty;  // Teamarr league info
        var channelName = program.ChannelName?.ToLowerInvariant() ?? string.Empty;  // Teamarr matchup as channel name
        
        // Include episode title and channel name for teamarr events which store league/matchup info there
        var combinedText = $"{title} {description} {episodeTitle} {channelName}";

        foreach (var sub in subscriptions.OrderBy(s => s.SortOrder))
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
                        // Use word boundary matching for leagues to avoid partial matches
                        // e.g., "premier league" shouldn't match "Afghanistan Premier League"
                        isMatch = MatchesWithWordBoundary(combinedText, pattern);
                        
                        // Try common league aliases (e.g., "ncaa" = "college")
                        if (!isMatch)
                        {
                            var expandedPatterns = GetLeaguePatternAliases(pattern);
                            foreach (var alias in expandedPatterns)
                            {
                                if (MatchesWithWordBoundary(combinedText, alias))
                                {
                                    isMatch = true;
                                    _logger.LogDebug("Matched '{Title}' to subscription '{Sub}' via alias '{Alias}'",
                                        program.Name, sub.Name, alias);
                                    break;
                                }
                            }
                        }
                        
                        // Also check for teamarr-style EpisodeTitle which has just the league abbreviation (e.g., "NBA")
                        // If pattern is "nba basketball", also try just "nba" against episodeTitle
                        if (!isMatch && !string.IsNullOrEmpty(episodeTitle))
                        {
                            // Get primary keyword from pattern (e.g., "nba" from "nba basketball")
                            var patternWords = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            foreach (var word in patternWords)
                            {
                                if (word.Length >= 3 && MatchesWithWordBoundary(episodeTitle, word))
                                {
                                    isMatch = true;
                                    _logger.LogDebug("Matched '{Title}' to subscription '{Sub}' via EpisodeTitle '{Episode}'",
                                        program.Name, sub.Name, program.EpisodeTitle);
                                    break;
                                }
                            }
                        }
                    }
                    break;

                case SubscriptionType.Event:
                    if (IsRegexPattern(pattern))
                    {
                        isMatch = MatchesRegex(combinedText, pattern);
                    }
                    else
                    {
                        // Events can use simple contains since they're more specific
                        isMatch = combinedText.Contains(pattern, StringComparison.OrdinalIgnoreCase);
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
        var title = program.Name ?? string.Empty;
        var titleLower = title.ToLowerInvariant();
        var description = program.Overview?.ToLowerInvariant() ?? string.Empty;
        var combinedText = $"{titleLower} {description}";

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

        // HARD RULE: Only record LIVE content for ALL subscription types
        // Teams = live games only, Leagues = live games only, Events = live events only
        if (!IsLiveContent(title, program))
        {
            _logger.LogDebug("Excluding '{Title}' - not live content", program.Name);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Check if content is LIVE - applies to all subscription types
    /// </summary>
    private static bool IsLiveContent(string title, ProgramInfo program)
    {
        // Check program flags first
        if (program.IsLive)
        {
            return true;
        }

        // Include EpisodeTitle for sport type detection (teamarr stores league info there)
        var titleWithEpisode = $"{title} {program.EpisodeTitle}";
        var titleLower = titleWithEpisode.ToLower();

        // Explicit LIVE indicators in title
        if (LiveIndicatorPattern.IsMatch(titleWithEpisode))
        {
            return true;
        }

        // Main Card / Prelims for events
        if (Regex.IsMatch(titleWithEpisode, @":\s*(main\s*card|prelims?)\s*$", RegexOptions.IgnoreCase))
        {
            return true;
        }

        // Teamarr filler: "Game Starting at X:XX PM EST" = placeholder before the real game entry
        // "Game Complete" = finished game. Both are not the actual game broadcast.
        if (Regex.IsMatch(titleLower, @"\bgame\s+starting\s+at\b") ||
            Regex.IsMatch(titleLower, @"\bgame\s+complete\b"))
        {
            return false;
        }

        // Exclude obvious non-live content
        var nonLivePatterns = new[] {
            "channel", "network", "tv", "24/7",            // Channel names
            "show", "magazine", "stories", "story",        // Shows
            "preview", "review", "analysis", "breakdown",  // Analysis content
            "flashback", "reloaded", "archive", "repeat",  // Replays
            "greatest", "best of", "top 10", "top ten",    // Compilations
            "fight o'clock", "fight pass",                 // UFC filler
            "netbusters", "goals of", "goal of",           // Highlight shows
            "full fight", "full match",                    // Fight/match replays
            "q&a", "q a", "faceoff", "faceoffs",           // Interviews/promos
            "expects", "expects finish", "wants revenge",  // Interview headlines
            "what's next", "what s next", "who's next", "who s next",  // More interviews
            "match hls", "hls 25", "hls 24",               // Abbreviated highlights
            "postgame", "pregame", "halftime",             // Pre/post shows
            "afghanistan", "lanka", "india premier",       // Wrong leagues
            "t20", "cricket", "karate", "kabaddi",         // Wrong sports with "Premier League"
            "embedded", "countdown", "weigh-in",           // Event promos
            "2017/", "2018/", "2019/", "2020/", "2021/", "2022/", "2023/", "2024/",  // Past season replays
            " hl ", " hl:", ":hl ", " hls ",                // Highlights abbreviation
            "(2008)", "(2009)", "(2010)", "(2011)", "(2012)", "(2013)", "(2014)",  // Old year replays
            "(2015)", "(2016)", "(2017)", "(2018)", "(2019)", "(2020)", "(2021)",
            "(2022)", "(2023)", "(2024)"
        };
        
        // Also exclude past UFC events (UFC 273, 311, 323 etc. are old)
        // Only allow current/recent UFC numbers (325, 326+)
        if (Regex.IsMatch(titleLower, @"ufc\s*(1\d\d|2[0-4]\d|2[5-9]\d|3[01]\d|32[0-4])\b"))
        {
            return false; // Past UFC event number (< 325)
        }
        
        // Season format "XX/YY:" (e.g., "NBA 25/26:", "Serie A 25/26:") without LIVE = scheduled rebroadcast
        if (Regex.IsMatch(titleWithEpisode, @"\b\d{2}/\d{2}\s*:"))
        {
            // Only allow if it also has LIVE somewhere
            if (!LiveIndicatorPattern.IsMatch(titleWithEpisode))
            {
                return false;
            }
        }

        foreach (var pattern in nonLivePatterns)
        {
            if (titleLower.Contains(pattern))
            {
                return false;
            }
        }

        // For sports games with vs/v/@/at pattern
        // Use time-based heuristics to determine if likely live
        if (Regex.IsMatch(titleWithEpisode, @"\s+(vs\.?|v\.?|@|at)\s+", RegexOptions.IgnoreCase))
        {
            // If explicitly marked live, always accept
            if (program.IsLive)
            {
                return true;
            }
            
            // Use time-based heuristics for game broadcasts
            // Pass full context including EpisodeTitle for sport type detection
            return IsLikelyLiveGameTime(titleWithEpisode, program.StartDate);
        }
        
        // "Team at Team" format without prefix - likely current game
        // Use titleWithEpisode to catch teamarr-style team names
        if (Regex.IsMatch(titleWithEpisode, @"^[A-Z][a-zA-Z\s\(\)\-0-9]+ at [A-Z][a-zA-Z\s\(\)\-0-9]+"))
        {
            return true;
        }

        // Generic titles without clear indicators - exclude
        // "Premier League", "NBA Basketball", etc. alone are not specific enough
        // But don't exclude if it's a teamarr event with specific matchup info
        var baseTitle = title.ToLower();  // Just the original title for this check
        if (Regex.IsMatch(baseTitle, @"^(premier league|nba|nfl|nhl|mlb|ncaa|college|serie a|la liga|bundesliga|mma|ufc)(\s+basketball|\s+football|\s+hockey)?$", RegexOptions.IgnoreCase))
        {
            return false;
        }

        // If program is marked as new/premiere, consider it live
        if (program.IsPremiere)
        {
            return true;
        }

        // Default: if no live indicator found, exclude
        // This is strict but ensures only truly live content
        return false;
    }

    // Pattern for LIVE indicators in title
    private static readonly Regex LiveIndicatorPattern = new(
        @"\blive\b|\(live\)|\[live\]|^live\s*:|:\s*live\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // For events (UFC, WWE, etc.), use STRICT ALLOWLIST approach
    // Only record if the title explicitly indicates LIVE broadcast
    private static readonly Regex LiveEventPattern = new(
        @"^live\s*[:\-]|[:\-]\s*live\b|\blive\s*$|\(live\)|\[live\]|" + // "Live:", ": Live", ends with "Live"
        @"^(ufc|wwe|aew)\s+\d+\s*[:\-]?\s*(main\s*card|prelims?)|" +    // "UFC 325: Main Card" at start
        @":\s*(main\s*card|prelims?|early\s*prelims?)\s*$",             // Ends with ": Main Card"
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Determines if a game is likely live based on time-of-day heuristics.
    /// USA sports: 12:00 PM - 11:59 PM EST (5:00 PM - 4:59 AM UTC)
    /// European football: 6:00 AM - 4:00 PM EST (11:00 AM - 9:00 PM UTC)
    /// </summary>
    private static bool IsLikelyLiveGameTime(string title, DateTime startTimeUtc)
    {
        var titleLower = title.ToLowerInvariant();
        
        // Detect sport type from title
        var isEuropeanFootball = 
            titleLower.Contains("premier league") ||
            titleLower.Contains("serie a") ||
            titleLower.Contains("la liga") ||
            titleLower.Contains("bundesliga") ||
            titleLower.Contains("ligue 1") ||
            titleLower.Contains("champions league") ||
            titleLower.Contains("europa league") ||
            titleLower.Contains("carabao") ||
            titleLower.Contains("fa cup") ||
            titleLower.Contains("epl") ||
            titleLower.Contains("pl:");
        
        var isUsaSport =
            titleLower.Contains("nba") ||
            titleLower.Contains("nfl") ||
            titleLower.Contains("nhl") ||
            titleLower.Contains("mlb") ||
            titleLower.Contains("celtics") ||
            titleLower.Contains("lakers") ||
            titleLower.Contains("warriors") ||
            titleLower.Contains("mavericks") ||
            titleLower.Contains("rockets") ||
            titleLower.Contains("ncaa") ||
            titleLower.Contains("college");
        
        // Get configured region for time-based filtering
        var config = Plugin.Instance?.Configuration;
        var region = config?.PrimaryRegion ?? "USA";
        
        // Use region-aware time windows
        return RegionTimeWindows.IsWithinLiveWindow(region, startTimeUtc.Hour, isEuropeanFootball);
    }

    private static bool IsNonLiveEventContent(string title)
    {
        // STRICT: Only record if it has LIVE indicators
        // Everything else is assumed to be replays/previews/interviews
        if (LiveEventPattern.IsMatch(title))
        {
            return false; // It's live, don't exclude
        }

        // No LIVE indicator = exclude (strict approach)
        return true;
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
            
            // Map SortOrder (0=highest) to Jellyfin priority (higher number = higher priority)
            Priority = Math.Max(1, 100 - subscription.SortOrder)
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
        parts.Add($"Priority: #{subscription.SortOrder + 1}");

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

    /// <summary>
    /// Builds a dedup key from program name, channel, and start time so we don't create
    /// duplicate timers when program IDs change across EPG refreshes or when scans run multiple times.
    /// </summary>
    private static string BuildTimerKey(string? name, string? channelId, DateTime startDate)
    {
        var normalizedName = (name ?? string.Empty).ToLowerInvariant().Trim();
        var channel = channelId ?? string.Empty;
        var roundedTime = new DateTime(startDate.Year, startDate.Month, startDate.Day,
            startDate.Hour, startDate.Minute, 0, DateTimeKind.Utc);
        return $"{normalizedName}|{channel}|{roundedTime:yyyyMMddHHmm}";
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
    
    private static bool MatchesWithWordBoundary(string text, string pattern)
    {
        // For leagues like "Premier League", ensure we match at word boundaries
        // This prevents "premier league" from matching "Afghanistan Premier League"
        try
        {
            // Check if pattern appears at the start, or preceded by non-word char
            var escapedPattern = Regex.Escape(pattern);
            
            // Match pattern that starts the text, or is preceded by start/colon/space
            // and is followed by end/colon/space/dash
            var wordBoundaryPattern = $@"(?:^|[:\s])({escapedPattern})(?:[:\s\-]|$)";
            
            return Regex.IsMatch(text, wordBoundaryPattern, RegexOptions.IgnoreCase);
        }
        catch
        {
            // Fallback to simple contains
            return text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }
    }
    
    /// <summary>
    /// Get common aliases for league patterns to handle variations in EPG data.
    /// e.g., "ncaa basketball" should also match "college basketball"
    /// </summary>
    private static IEnumerable<string> GetLeaguePatternAliases(string pattern)
    {
        var aliases = new List<string> { pattern };
        var patternLower = pattern.ToLowerInvariant();
        
        // NCAA / College equivalence + gendered variants (NCAAM, NCAAW)
        if (patternLower.Contains("ncaa"))
        {
            aliases.Add(patternLower.Replace("ncaa", "college"));
            aliases.Add("ncaam");   // NCAA Men's
            aliases.Add("ncaaw");   // NCAA Women's
            aliases.Add("ncaa basketball");
            aliases.Add("ncaa football");
            aliases.Add("college basketball");
            aliases.Add("college football");
        }
        else if (patternLower.Contains("college"))
        {
            aliases.Add(patternLower.Replace("college", "ncaa"));
            aliases.Add("ncaam");
            aliases.Add("ncaaw");
        }
        
        // NBA equivalence
        if (patternLower == "nba basketball" || patternLower == "nba")
        {
            aliases.Add("nba");
            aliases.Add("nba basketball");
        }
        
        // NFL equivalence
        if (patternLower == "nfl football" || patternLower == "nfl")
        {
            aliases.Add("nfl");
            aliases.Add("nfl football");
        }
        
        // NHL equivalence
        if (patternLower == "nhl hockey" || patternLower == "nhl")
        {
            aliases.Add("nhl");
            aliases.Add("nhl hockey");
        }
        
        // MLB equivalence
        if (patternLower == "mlb baseball" || patternLower == "mlb")
        {
            aliases.Add("mlb");
            aliases.Add("mlb baseball");
        }
        
        // Premier League / EPL equivalence
        if (patternLower.Contains("premier league"))
        {
            aliases.Add("epl");
            aliases.Add("english premier league");
        }
        else if (patternLower == "epl")
        {
            aliases.Add("premier league");
            aliases.Add("english premier league");
        }
        
        // Serie A variants
        if (patternLower.Contains("serie a"))
        {
            aliases.Add("italian serie a");
            aliases.Add("serie a soccer");
        }
        
        // March Madness / NCAA Tournament
        if (patternLower.Contains("march madness"))
        {
            aliases.Add("ncaa tournament");
            aliases.Add("college basketball");
        }
        
        return aliases.Distinct();
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
    
    /// <summary>Gets or sets the episode title (often contains league info from teamarr).</summary>
    public string? EpisodeTitle { get; set; }
}
