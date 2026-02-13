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
        _logger.LogInformation("Clearing scheduled programs cache");
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
    /// Uses BuildTimerKey (name+channel+time) as the SOLE dedup mechanism to handle
    /// programme GUID changes across guide refreshes. Passes existing timer windows
    /// to the scheduler so it respects already-occupied slots.
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
        
        // Use ONLY BuildTimerKey for dedup (stable across guide refreshes, unlike programme GUIDs)
        var existingTimerKeys = new HashSet<string>(
            existingTimers.Select(t => BuildTimerKey(t.Name, t.ChannelId.ToString(), t.StartDate)),
            StringComparer.OrdinalIgnoreCase);

        // Step 2: Find all matching programs
        var allMatches = FindAllMatches(programs, subscriptions, cancellationToken);
        result.MatchesFound = allMatches.Count;

        // Step 3: Separate into new vs already-scheduled using ONLY timer keys
        var newMatches = new List<MatchedProgram>();
        var alreadyScheduledCount = 0;

        foreach (var match in allMatches)
        {
            var timerKey = BuildTimerKey(match.Program.Name, match.Program.ChannelGuid.ToString(), match.Program.StartDate);
            if (existingTimerKeys.Contains(timerKey))
            {
                alreadyScheduledCount++;
                _logger.LogDebug("Already scheduled: '{Title}' (key: {Key})", match.Program.Name, timerKey);
            }
            else
            {
                newMatches.Add(match);
            }
        }

        _logger.LogInformation(
            "Confirmation scan: {Total} matches ({New} new, {Existing} already scheduled)",
            allMatches.Count, newMatches.Count, alreadyScheduledCount);

        // Step 4: Schedule new matches — pass existing timer windows so scheduler sees occupied slots
        var scheduledCount = 0;
        var alreadyExisted = 0;
        if (newMatches.Count > 0)
        {
            // Build existing timer time windows for the scheduler to account for
            var existingSlots = existingTimers
                .Where(t => t.StartDate > DateTime.UtcNow)
                .Select(t => (Start: t.StartDate, End: t.EndDate))
                .ToList();

            var schedule = _smartScheduler.CreateSchedule(newMatches, maxConcurrent, existingSlots);
            foreach (var recording in schedule)
            {
                try
                {
                    await ScheduleRecordingAsync(recording, cancellationToken).ConfigureAwait(false);
                    scheduledCount++;

                    _logger.LogInformation(
                        "Scheduled (new): '{Title}' at {Time} on {Channel}",
                        recording.Title,
                        recording.StartTime.ToLocalTime().ToString("g"),
                        recording.ChannelName);
                }
                catch (ArgumentException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                {
                    alreadyExisted++;
                    _logger.LogDebug("Timer already exists for '{Title}' - skipping", recording.Title);
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

        foreach (var timer in existingTimers)
        {
            // Only check future timers that we created (identified by "Subscription:" in overview)
            if (timer.StartDate < now) continue;
            var overview = timer.Overview?.ToLowerInvariant() ?? string.Empty;
            if (!overview.Contains("subscription:")) continue;

            var timerKey = BuildTimerKey(timer.Name, timer.ChannelId.ToString(), timer.StartDate);
            if (!matchedProgramKeys.Contains(timerKey))
            {
                _logger.LogWarning(
                    "Stale timer detected: '{Title}' at {Time} - no matching EPG program, cancelling",
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
        var scoredCount = 0;
        var noSubCount = 0;
        var excludedCount = 0;

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
            scoredCount++;

            var matchedSub = FindMatchingSubscription(program, scored, subscriptions);
            if (matchedSub == null)
            {
                noSubCount++;
                _logger.LogDebug(
                    "No sub: '{Title}' | score={Score} | genres=[{Genres}] | episode='{Episode}' | isLive={IsLive}",
                    program.Name, scored.Score,
                    string.Join(", ", program.Genres ?? Array.Empty<string>()),
                    program.EpisodeTitle, program.IsLive);
                continue;
            }

            if (ShouldExclude(program, scored, matchedSub))
            {
                excludedCount++;
                _logger.LogDebug("Excluded: '{Title}' matched sub '{Sub}' but filtered out (isLive={IsLive}, isReplay={IsReplay})",
                    program.Name, matchedSub.Name, program.IsLive, scored.IsReplay);
                continue;
            }

            _logger.LogInformation(
                "MATCH: '{Title}' -> sub '{Sub}' ({SubType}) | score={Score}, genres=[{Genres}], episode='{Episode}', isLive={IsLive}",
                program.Name, matchedSub.Name, matchedSub.Type, scored.Score,
                string.Join(", ", program.Genres ?? Array.Empty<string>()),
                program.EpisodeTitle, program.IsLive);

            matches.Add(new MatchedProgram
            {
                Program = program,
                Scored = scored,
                Subscription = matchedSub
            });
        }

        _logger.LogInformation(
            "Match summary: {Scored} passed scoring, {Matched} matched subscriptions, {NoSub} no sub, {Excluded} excluded",
            scoredCount, matches.Count, noSubCount, excludedCount);

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
                            EpisodeTitle = program.EpisodeTitle,  // Teamarr stores league info here (e.g., "NBA")
                            Genres = program.Genres ?? Array.Empty<string>()
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
                        // Build league context from structured Teamarr EPG data:
                        // - Genres (from <category> tags): ["Sports", "Basketball", "College"]
                        // - EpisodeTitle (from <sub-title>): "NCAAM", "NCAA Baseball", "La Liga"
                        // - Title (which now includes league prefix): "NCAAM Brown Bears at Harvard Crimson"
                        // Do NOT include channelName (it's the matchup, not the league)
                        var genresLower = program.Genres?.Select(g => g.ToLowerInvariant()).ToArray() ?? Array.Empty<string>();
                        var leagueContext = $"{title} {episodeTitle} {string.Join(" ", genresLower)}";
                        
                        // All significant words in pattern must appear in league context (AND logic)
                        // e.g., "ncaa basketball" requires BOTH "ncaa" AND "basketball" to appear
                        // This prevents "ncaa basketball" from matching "NCAA Baseball"
                        var patternWords = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (patternWords.Length > 0 && patternWords.All(w =>
                            leagueContext.Contains(w, StringComparison.OrdinalIgnoreCase)))
                        {
                            isMatch = true;
                        }
                        
                        // Try aliases with same AND logic
                        if (!isMatch)
                        {
                            var expandedPatterns = GetLeaguePatternAliases(pattern);
                            foreach (var alias in expandedPatterns)
                            {
                                if (alias == pattern) continue; // Already checked above
                                var aliasWords = alias.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                if (aliasWords.Length > 0 && aliasWords.All(w =>
                                    leagueContext.Contains(w, StringComparison.OrdinalIgnoreCase)))
                                {
                                    isMatch = true;
                                    _logger.LogDebug("Matched '{Title}' to subscription '{Sub}' via alias '{Alias}'",
                                        program.Name, sub.Name, alias);
                                    break;
                                }
                            }
                        }
                        
                        // Also try full pattern as substring of leagueContext
                        if (!isMatch)
                        {
                            isMatch = leagueContext.Contains(pattern, StringComparison.OrdinalIgnoreCase);
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
    /// Check if content is LIVE - uses multiple signals from Teamarr's structured EPG.
    /// Teamarr marks actual game broadcasts with &lt;live/&gt; tag and sport-specific &lt;category&gt; tags.
    /// Filler (Game Starting, Game Complete, Programming) has neither.
    /// </summary>
    private static bool IsLiveContent(string title, ProgramInfo program)
    {
        // Signal 1: Teamarr sets <live/> on actual game broadcasts only
        if (program.IsLive)
        {
            return true;
        }

        // Signal 2: Explicit LIVE text in title
        if (LiveIndicatorPattern.IsMatch(title))
        {
            return true;
        }

        // Signal 3: Teamarr sets <category> tags (Genres) ONLY on live game programmes, not filler.
        // If programme has sport-specific genres (Baseball, Basketball, Hockey, Soccer, etc.)
        // AND a matchup pattern (at/vs/@) in the title, it's a live game broadcast.
        // This handles the case where Jellyfin's IsLive doesn't map from XMLTV <live/> tag.
        if (program.Genres != null && program.Genres.Length > 0)
        {
            var hasRelevantGenre = program.Genres.Any(g =>
                !string.Equals(g, "Sports", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(g, "Show", StringComparison.OrdinalIgnoreCase));

            if (hasRelevantGenre || program.IsSports)
            {
                // Has sport-specific category — check for matchup pattern in title
                if (MatchupPattern.IsMatch(title))
                {
                    return true;
                }
            }
        }

        // Teamarr filler exclusions: "Game Starting at", "Game Complete", "Programming"
        // These have NO genres and NO live tag, so they naturally fall through to false here.
        return false;
    }

    // Pattern for matchup indicators in title (vs/@/at between team names)
    private static readonly Regex MatchupPattern = new(
        @"\s+(vs\.?|v\.?|@|at)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Pattern for LIVE indicators in title
    private static readonly Regex LiveIndicatorPattern = new(
        @"\blive\b|\(live\)|\[live\]|^live\s*:|:\s*live\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
    /// Get sport-specific aliases for league patterns.
    /// IMPORTANT: Aliases are sport-specific to prevent cross-sport matching.
    /// "ncaa basketball" should NOT expand to bare "ncaa" (which matches NCAA Baseball).
    /// All aliases are matched using AND logic (all words must appear).
    /// </summary>
    private static IEnumerable<string> GetLeaguePatternAliases(string pattern)
    {
        var aliases = new List<string> { pattern };
        var patternLower = pattern.ToLowerInvariant();
        
        // NCAA Basketball variants (sport-specific, no cross-sport bleed)
        if (patternLower == "ncaa basketball" || patternLower == "college basketball")
        {
            aliases.Add("ncaa basketball");
            aliases.Add("college basketball");
            aliases.Add("ncaam");   // Teamarr uses this in EpisodeTitle
            aliases.Add("ncaaw");   // Women's
        }
        
        // NCAA Football variants (sport-specific)
        if (patternLower == "ncaa football" || patternLower == "college football")
        {
            aliases.Add("ncaa football");
            aliases.Add("college football");
        }
        
        // NCAA Baseball variants (sport-specific)
        if (patternLower == "ncaa baseball" || patternLower == "college baseball")
        {
            aliases.Add("ncaa baseball");
            aliases.Add("college baseball");
        }
        
        // NCAA Hockey variants (sport-specific)
        if (patternLower == "ncaa hockey" || patternLower == "college hockey")
        {
            aliases.Add("ncaa hockey");
            aliases.Add("college hockey");
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
        }
        
        // March Madness → NCAA Basketball Tournament
        if (patternLower.Contains("march madness"))
        {
            aliases.Add("ncaa tournament");
            aliases.Add("ncaam");
        }
        
        // Champions League
        if (patternLower.Contains("champions league"))
        {
            aliases.Add("ucl");
            aliases.Add("uefa champions league");
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

    /// <summary>Gets or sets the genres/categories from XMLTV (e.g., ["Sports", "Basketball", "College"]).</summary>
    public string[] Genres { get; set; } = Array.Empty<string>();
}
