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

    // Schedule for the current day only. End-of-day is 6 AM UTC next day
    // (covers late-night games that run past midnight in US time zones).
    // At 10 AM EST scan time, this gives ~20 hours of lookahead.
    private const int END_OF_DAY_UTC_HOUR = 6;
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
    /// Cancels all Jellyfin DVR timers created by Sports DVR.
    /// Uses the internal ManagedTimerKeys registry to identify our timers.
    /// </summary>
    public async Task<int> CancelSportsDvrTimersAsync(CancellationToken cancellationToken)
    {
        var timers = await GetExistingTimersAsync(cancellationToken).ConfigureAwait(false);
        var managedKeys = GetManagedTimerKeys();
        var cancelledCount = 0;

        foreach (var timer in timers)
        {
            if (timer.StartDate < DateTime.UtcNow) continue;

            var timerKey = BuildTimerKey(timer.Name, timer.ChannelId.ToString(), timer.StartDate);
            if (!managedKeys.Contains(timerKey)) continue;

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

        // Clear the registry since we just cancelled everything
        ClearManagedTimerKeys();
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

            _logger.LogInformation(
                "FULL EPG scan for {Count} subscriptions (max {Max} concurrent, today only)",
                subscriptions.Count,
                maxConcurrent);

            // Prune stale entries from the timer registry before scanning
            await PruneManagedTimerKeysAsync(cancellationToken).ConfigureAwait(false);

            // Always do a full scan — wipe old timers and rebuild from scratch.
            // Since we scan once daily (+ startup), every scan should build the
            // complete optimized schedule from the full EPG.
            result = await FullScanAsync(subscriptions, maxConcurrent, cancellationToken).ConfigureAwait(false);
            _lastFullRebuildAfterPurge = DateTime.UtcNow;
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
        _logger.LogInformation("Found {Count} programs in EPG (today only)", programs.Count);

        if (programs.Count == 0)
        {
            return result;
        }

        // Step 3: Find all matching programs
        var matches = FindAllMatches(programs, subscriptions, cancellationToken);
        result.MatchesFound = matches.Count;

        _logger.LogInformation("Found {Count} programs matching subscriptions", matches.Count);

        // Log structured game list grouped by league
        if (matches.Count > 0)
        {
            LogGameList(matches);
        }

        if (matches.Count == 0)
        {
            return result;
        }

        // Step 4: Build optimized schedule using raw EPG times
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
        _logger.LogInformation("Found {Count} programs in EPG (today)", programs.Count);

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
        var managedKeys = GetManagedTimerKeys();
        var matchedProgramKeys = new HashSet<string>(
            allMatches.Select(m => BuildTimerKey(m.Program.Name, m.Program.ChannelGuid.ToString(), m.Program.StartDate)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var timer in existingTimers)
        {
            if (timer.StartDate < now) continue;

            var timerKey = BuildTimerKey(timer.Name, timer.ChannelId.ToString(), timer.StartDate);
            if (!managedKeys.Contains(timerKey)) continue;

            if (!matchedProgramKeys.Contains(timerKey))
            {
                _logger.LogWarning(
                    "Stale timer detected: '{Title}' at {Time} - no matching EPG program, cancelling",
                    timer.Name,
                    timer.StartDate.ToLocalTime().ToString("g"));

                try
                {
                    await _liveTvManager.CancelTimer(timer.Id).ConfigureAwait(false);
                    RemoveManagedTimerKey(timerKey);
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
        var skippedEmpty = 0;
        var noSubCount = 0;
        var excludedCount = 0;

        foreach (var program in programs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip programs with no meaningful title (Teamarr filler when no game is on)
            if (string.IsNullOrWhiteSpace(program.Name))
            {
                skippedEmpty++;
                continue;
            }

            // Use scorer for metadata extraction (team names, league) — NOT as a gate
            var descWithEpisode = string.Join(" ", new[] { 
                program.EpisodeTitle, 
                program.Overview 
            }.Where(s => !string.IsNullOrEmpty(s)));

            var scored = _sportsScorer.Score(
                program.Name ?? string.Empty,
                program.ChannelName,
                descWithEpisode,
                program.IsSports);

            var matchedSub = FindMatchingSubscription(program, scored, subscriptions);
            if (matchedSub == null)
            {
                noSubCount++;
                _logger.LogDebug(
                    "NO MATCH: '{Title}' | episode='{Episode}' | start={Start}",
                    program.Name,
                    program.EpisodeTitle, program.StartDate.ToLocalTime().ToString("g"));
                continue;
            }

            if (ShouldExclude(program, scored, matchedSub))
            {
                excludedCount++;
                _logger.LogInformation("EXCLUDED: '{Title}' matched sub '{Sub}' but filtered out (isLive={IsLive}, isReplay={IsReplay})",
                    program.Name, matchedSub.Name, program.IsLive, scored.IsReplay);
                continue;
            }

            _logger.LogInformation(
                "MATCH: '{Title}' -> sub '{Sub}' ({SubType}) | episode='{Episode}', isLive={IsLive}",
                program.Name, matchedSub.Name, matchedSub.Type,
                program.EpisodeTitle, program.IsLive);

            matches.Add(new MatchedProgram
            {
                Program = program,
                Scored = scored,
                Subscription = matchedSub
            });
        }

        _logger.LogInformation(
            "Match summary: {Total} programs scanned, {Matched} matched subscriptions, {NoSub} no sub, {Excluded} excluded, {Empty} blank titles skipped",
            programs.Count, matches.Count, noSubCount, excludedCount, skippedEmpty);

        return matches;
    }

    /// <summary>
    /// Logs a structured summary of all matched games grouped by league.
    /// </summary>
    private void LogGameList(List<MatchedProgram> matches)
    {
        _logger.LogInformation("--- TODAY'S MATCHED GAMES ---");

        var byLeague = matches
            .GroupBy(m => m.Scored.DetectedLeague ?? m.Program.EpisodeTitle ?? "Other")
            .OrderBy(g => g.Key);

        foreach (var group in byLeague)
        {
            _logger.LogInformation("  [{League}]", group.Key);
            foreach (var m in group.OrderBy(g => g.Program.StartDate))
            {
                var hasMatchup = MatchupPattern.IsMatch(m.Program.Name ?? "");
                var type = hasMatchup ? "Game" : "Event";
                var teams = !string.IsNullOrEmpty(m.Scored.Team1) && !string.IsNullOrEmpty(m.Scored.Team2)
                    ? $"{m.Scored.Team1} vs {m.Scored.Team2}"
                    : m.Program.Name ?? "Unknown";

                _logger.LogInformation(
                    "    {Time} | {Type} | {Teams} | ch:{Channel} | sub:#{Sort} {SubName}",
                    m.Program.StartDate.ToLocalTime().ToString("g"),
                    type,
                    teams,
                    m.Program.ChannelName,
                    m.Subscription.SortOrder + 1,
                    m.Subscription.Name);
            }
        }

        _logger.LogInformation("--- END GAME LIST ({Count} total) ---", matches.Count);
    }

    private async Task<List<ProgramInfo>> GetUpcomingProgramsAsync(CancellationToken cancellationToken)
    {
        var result = new List<ProgramInfo>();
        var config = Plugin.Instance?.Configuration;
        var ignoredChannels = config?.IgnoredChannels ?? string.Empty;

        // Calculate end-of-day: next occurrence of 6 AM UTC (1 AM EST / 10 PM PST).
        // This covers all US sports including late West Coast games.
        var now = DateTime.UtcNow;
        var endDate = now.Date.AddHours(END_OF_DAY_UTC_HOUR);
        if (endDate <= now)
        {
            endDate = endDate.AddDays(1);
        }

        try
        {
            var channelQuery = new LiveTvChannelQuery { };
            var dtoOptions = new DtoOptions(true);
            var channels = _liveTvManager.GetInternalChannels(channelQuery, dtoOptions, cancellationToken);

            var ignoredCount = 0;
            _logger.LogDebug("Scanning {Count} Live TV channels", channels.Items.Count);

            foreach (var channel in channels.Items)
            {
                // Skip channels on the ignore list
                var ltvChannel = channel as MediaBrowser.Controller.LiveTv.LiveTvChannel;
                var channelNumber = ltvChannel?.Number;
                if (IsChannelIgnored(channelNumber, ignoredChannels))
                {
                    ignoredCount++;
                    continue;
                }

                try
                {
                    // Only retrieve future programs — never schedule games already in progress.
                    // We want full recordings from tip-off/kickoff, not partial mid-game joins.
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
                            IsNew = program.IsNews ?? false, // Jellyfin 10.11 has IsNews, not IsNew
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
            if (ignoredCount > 0)
            {
                _logger.LogInformation("Skipped {Count} ignored channels", ignoredCount);
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
        
        // Include episode title, channel name, and genres for matching.
        // Genres are critical for event subscriptions (e.g., "Winter Olympics", "Olympics" 
        // appear as <category> tags but may not be in the title/description).
        var genresText = program.Genres != null ? string.Join(" ", program.Genres) : string.Empty;
        var combinedText = $"{title} {description} {episodeTitle} {channelName} {genresText.ToLowerInvariant()}";

        foreach (var sub in subscriptions.OrderBy(s => s.SortOrder))
        {
            var pattern = sub.MatchPattern?.ToLowerInvariant() ?? sub.Name.ToLowerInvariant();
            bool isMatch = false;

            switch (sub.Type)
            {
                case SubscriptionType.Team:
                    var resolvedPattern = _aliasService.ResolveAlias(pattern).ToLowerInvariant();
                    
                    // Check if the full pattern appears as a whole word in the combined text
                    if (ContainsWholeWord(combinedText, resolvedPattern) ||
                        ContainsWholeWord(combinedText, pattern))
                    {
                        isMatch = true;
                        _logger.LogDebug("Matched '{Title}' to subscription '{Sub}' via pattern '{Pattern}'",
                            program.Name, sub.Name, pattern);
                    }
                    // Check extracted team names using whole-word matching
                    else if (!string.IsNullOrEmpty(scored.Team1) && !string.IsNullOrEmpty(scored.Team2))
                    {
                        var team1 = _aliasService.ResolveAlias(scored.Team1.ToLowerInvariant()).ToLowerInvariant();
                        var team2 = _aliasService.ResolveAlias(scored.Team2.ToLowerInvariant()).ToLowerInvariant();
                        
                        if (!string.IsNullOrEmpty(team1) && !string.IsNullOrEmpty(team2))
                        {
                            if (ContainsWholeWord(team1, resolvedPattern) || 
                                ContainsWholeWord(team2, resolvedPattern) ||
                                ContainsWholeWord(team1, pattern) ||
                                ContainsWholeWord(team2, pattern))
                            {
                                isMatch = true;
                                _logger.LogDebug("Matched '{Title}' to subscription '{Sub}' via team extraction (teams: {Team1} vs {Team2})",
                                    program.Name, sub.Name, team1, team2);
                            }
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
                        
                        // All significant words in pattern must appear as WHOLE WORDS in league context.
                        // Uses \b word boundaries to prevent "ucl" matching inside "UCLA".
                        var patternWords = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (patternWords.Length > 0 && patternWords.All(w =>
                            ContainsWholeWord(leagueContext, w)))
                        {
                            isMatch = true;
                        }
                        
                        // Try aliases with same whole-word AND logic
                        if (!isMatch)
                        {
                            var expandedPatterns = GetLeaguePatternAliases(pattern);
                            foreach (var alias in expandedPatterns)
                            {
                                if (alias == pattern) continue;
                                var aliasWords = alias.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                if (aliasWords.Length > 0 && aliasWords.All(w =>
                                    ContainsWholeWord(leagueContext, w)))
                                {
                                    isMatch = true;
                                    _logger.LogDebug("Matched '{Title}' to subscription '{Sub}' via alias '{Alias}'",
                                        program.Name, sub.Name, alias);
                                    break;
                                }
                            }
                        }
                        
                        // Also try full pattern as whole-word match in leagueContext
                        if (!isMatch)
                        {
                            isMatch = ContainsWholeWord(leagueContext, pattern);
                        }
                        
                        // Exclude women's sports from men's league subscriptions.
                        if (isMatch && IsWomensSport(title, episodeTitle))
                        {
                            var patLower = pattern.ToLowerInvariant();
                            if (!patLower.Contains("women") && !patLower.Contains("wnba") && !patLower.Contains("ncaaw"))
                            {
                                _logger.LogDebug("Excluding '{Title}' from '{Sub}' — women's sport, subscription is not women-specific",
                                    program.Name, sub.Name);
                                isMatch = false;
                            }
                        }

                        // Prevent cross-competition matching within UEFA.
                        // A "Champions League" subscription should not match Europa League or Conference League.
                        if (isMatch)
                        {
                            var subNameLower = sub.Name.ToLowerInvariant();
                            if (subNameLower.Contains("champions league") && !subNameLower.Contains("europa") && !subNameLower.Contains("conference"))
                            {
                                if (ContainsWholeWord(leagueContext, "europa") || ContainsWholeWord(leagueContext, "conference"))
                                {
                                    _logger.LogDebug("Excluding '{Title}' from '{Sub}' — different UEFA competition",
                                        program.Name, sub.Name);
                                    isMatch = false;
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
        // This handles the case where Jellyfin's IsLive doesn't map from XMLTV <live/> tag.
        //
        // Two paths:
        // 3a: Event-level genres (Olympics, World Cup) — these are live without needing a matchup pattern
        // 3b: Sport-specific genres (Baseball, Basketball, etc.) + matchup pattern in title or subtitle
        if (program.Genres != null && program.Genres.Length > 0)
        {
            // 3a: Event-level genres are strong enough on their own (no matchup needed)
            var hasEventGenre = program.Genres.Any(g =>
                (g != null && g.Contains("Olympic", StringComparison.OrdinalIgnoreCase)) ||
                (g != null && g.Contains("World Cup", StringComparison.OrdinalIgnoreCase)) ||
                (g != null && g.Contains("World Series", StringComparison.OrdinalIgnoreCase)) ||
                (g != null && g.Contains("Super Bowl", StringComparison.OrdinalIgnoreCase)) ||
                (g != null && g.Contains("Championship", StringComparison.OrdinalIgnoreCase)) ||
                (g != null && g.Contains("All-Star", StringComparison.OrdinalIgnoreCase)) ||
                (g != null && g.Contains("All Star", StringComparison.OrdinalIgnoreCase)));

            if (hasEventGenre)
            {
                return true;
            }

            // 3b: Sport-specific genres + matchup pattern in title OR subtitle
            var hasRelevantGenre = program.Genres.Any(g =>
                !string.Equals(g, "Sports", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(g, "Show", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(g, "Sports Event", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(g, "Sporting Event", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(g, "Episode", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(g, "Series", StringComparison.OrdinalIgnoreCase));

            if (hasRelevantGenre || program.IsSports)
            {
                // Check for matchup pattern in title OR subtitle (EpisodeTitle)
                if (MatchupPattern.IsMatch(title))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(program.EpisodeTitle) && MatchupPattern.IsMatch(program.EpisodeTitle))
                {
                    return true;
                }
            }
        }

        // Teamarr filler exclusions: "Game Starting at", "Game Complete", "Programming"
        // These have NO genres and NO live tag, so they naturally fall through to false here.
        return false;
    }

    // Pattern for matchup indicators (vs/@/at between team names)
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

        var defaults = await _liveTvManager.GetNewTimerDefaults(cancellationToken).ConfigureAwait(false);

        var timerInfo = new TimerInfoDto
        {
            Name = program.Name,
            Overview = program.Overview,
            ChannelId = program.ChannelGuid,
            ProgramId = program.Id,
            StartDate = program.StartDate,
            EndDate = program.EndDate,
            ServiceName = program.ServiceName,
            
            PrePaddingSeconds = defaults.PrePaddingSeconds,
            PostPaddingSeconds = defaults.PostPaddingSeconds,
            
            IsPrePaddingRequired = defaults.IsPrePaddingRequired,
            IsPostPaddingRequired = defaults.IsPostPaddingRequired,
            
            Priority = Math.Max(1, 100 - subscription.SortOrder)
        };

        await _liveTvManager.CreateTimer(timerInfo, cancellationToken).ConfigureAwait(false);

        // Register the timer key so we can identify it as ours later
        var timerKey = BuildTimerKey(program.Name, program.ChannelGuid.ToString(), program.StartDate);
        AddManagedTimerKey(timerKey);
    }

    // --- Managed timer key registry (persisted in plugin config) ---

    private HashSet<string> GetManagedTimerKeys()
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.ManagedTimerKeys == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(config.ManagedTimerKeys, StringComparer.OrdinalIgnoreCase);
    }

    private void AddManagedTimerKey(string key)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null) return;
        config.ManagedTimerKeys ??= new List<string>();
        if (!config.ManagedTimerKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            config.ManagedTimerKeys.Add(key);
            Plugin.Instance!.SaveConfiguration();
        }
    }

    private void RemoveManagedTimerKey(string key)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.ManagedTimerKeys == null) return;
        var removed = config.ManagedTimerKeys.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            Plugin.Instance!.SaveConfiguration();
        }
    }

    private void ClearManagedTimerKeys()
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null) return;
        config.ManagedTimerKeys = new List<string>();
        Plugin.Instance!.SaveConfiguration();
    }

    /// <summary>
    /// Remove registry entries for timers that no longer exist in Jellyfin
    /// (past events, manually cancelled by user, etc.).
    /// </summary>
    private async Task PruneManagedTimerKeysAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config?.ManagedTimerKeys == null || config.ManagedTimerKeys.Count == 0) return;

        var timers = await GetExistingTimersAsync(cancellationToken).ConfigureAwait(false);
        var activeKeys = new HashSet<string>(
            timers.Select(t => BuildTimerKey(t.Name, t.ChannelId.ToString(), t.StartDate)),
            StringComparer.OrdinalIgnoreCase);

        var before = config.ManagedTimerKeys.Count;
        config.ManagedTimerKeys.RemoveAll(k => !activeKeys.Contains(k));
        var pruned = before - config.ManagedTimerKeys.Count;

        if (pruned > 0)
        {
            Plugin.Instance!.SaveConfiguration();
            _logger.LogInformation("Pruned {Count} stale entries from managed timer registry", pruned);
        }
    }

    // --- Channel ignore list ---

    /// <summary>
    /// Checks if a channel number should be skipped based on the ignore list config.
    /// Supports individual numbers ("1664") and ranges ("1-150").
    /// </summary>
    private static bool IsChannelIgnored(string? channelNumber, string? ignoredConfig)
    {
        if (string.IsNullOrWhiteSpace(channelNumber) || string.IsNullOrWhiteSpace(ignoredConfig))
            return false;

        if (!int.TryParse(channelNumber, out var chNum))
            return false;

        foreach (var part in ignoredConfig.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dashIndex = part.IndexOf('-');
            if (dashIndex > 0 && dashIndex < part.Length - 1)
            {
                // Range: "1-150"
                if (int.TryParse(part[..dashIndex], out var lo) &&
                    int.TryParse(part[(dashIndex + 1)..], out var hi) &&
                    chNum >= lo && chNum <= hi)
                {
                    return true;
                }
            }
            else
            {
                // Single number: "1664"
                if (int.TryParse(part, out var single) && chNum == single)
                {
                    return true;
                }
            }
        }

        return false;
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

    /// <summary>
    /// Checks if a word appears as a whole word in text (not as a substring of another word).
    /// Prevents "ucl" from matching "UCLA", "mls" from matching "htmls", etc.
    /// </summary>
    private static bool ContainsWholeWord(string text, string word)
    {
        return Regex.IsMatch(text, @"\b" + Regex.Escape(word) + @"\b", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Detects women's sports programs from title/episode clues.
    /// </summary>
    private static bool IsWomensSport(string title, string? episodeTitle)
    {
        var combined = $"{title} {episodeTitle ?? ""}";
        return Regex.IsMatch(combined, @"\bwomen'?s?\b|\bWNBA\b|\bNCAA\s*W\b|\bWPS\b|\bNWSL\b|\bW\.\s*Basketball\b", RegexOptions.IgnoreCase);
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
        
        // Champions League / UCL / UEFA (bidirectional)
        if (patternLower.Contains("champions league"))
        {
            aliases.Add("ucl");
            aliases.Add("uefa champions league");
            aliases.Add("uefa champions league soccer");
        }
        else if (patternLower == "ucl")
        {
            aliases.Add("uefa champions league");
            aliases.Add("champions league");
            aliases.Add("uefa champions league soccer");
        }
        else if (patternLower == "uefa")
        {
            aliases.Add("uefa champions league");
            aliases.Add("uefa champions league soccer");
        }
        
        // Europa League
        if (patternLower.Contains("europa league") && !patternLower.Contains("conference"))
        {
            aliases.Add("uefa europa league");
            aliases.Add("europa league soccer");
            aliases.Add("uefa europa league soccer");
        }
        
        // Conference League
        if (patternLower.Contains("conference league"))
        {
            aliases.Add("europa conference league");
            aliases.Add("uefa europa conference league");
        }
        
        // La Liga / Spanish
        if (patternLower == "la liga")
        {
            aliases.Add("spanish la liga");
            aliases.Add("la liga soccer");
        }
        
        // Bundesliga / German
        if (patternLower == "bundesliga")
        {
            aliases.Add("german bundesliga");
            aliases.Add("bundesliga soccer");
        }
        
        // Ligue 1 / French
        if (patternLower == "ligue 1")
        {
            aliases.Add("french ligue 1");
            aliases.Add("ligue 1 soccer");
        }
        
        // Liga MX / Mexican
        if (patternLower == "liga mx")
        {
            aliases.Add("mexican liga mx");
            aliases.Add("liga mx soccer");
        }
        
        // MLS
        if (patternLower == "mls")
        {
            aliases.Add("major league soccer");
            aliases.Add("mls soccer");
        }
        else if (patternLower == "major league soccer")
        {
            aliases.Add("mls");
            aliases.Add("mls soccer");
        }
        
        // WNBA
        if (patternLower == "wnba")
        {
            aliases.Add("wnba basketball");
            aliases.Add("women's nba");
        }
        
        // World Cup
        if (patternLower.Contains("world cup"))
        {
            aliases.Add("fifa world cup");
            aliases.Add("world cup qualifier");
            aliases.Add("world cup soccer");
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
