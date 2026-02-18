using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SportsDVR.Models;
using Jellyfin.Plugin.SportsDVR.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsDVR.Api;

/// <summary>
/// API controller for Sports DVR plugin.
/// </summary>
[ApiController]
[Route("SportsDVR")]
[Authorize(Policy = "RequiresElevation")]
public class SportsController : ControllerBase
{
    private readonly ILogger<SportsController> _logger;
    private readonly SubscriptionManager _subscriptionManager;
    private readonly EpgParser _epgParser;
    private readonly PatternMatcher _patternMatcher;
    private readonly ILiveTvManager _liveTvManager;
    private readonly SportsScorer _sportsScorer;
    private readonly RecordingScheduler _recordingScheduler;
    private readonly GuideCachePurgeService _guideCachePurgeService;

    /// <summary>
    /// Initializes a new instance of the <see cref="SportsController"/> class.
    /// </summary>
    public SportsController(
        ILogger<SportsController> logger,
        SubscriptionManager subscriptionManager,
        EpgParser epgParser,
        PatternMatcher patternMatcher,
        ILiveTvManager liveTvManager,
        SportsScorer sportsScorer,
        RecordingScheduler recordingScheduler,
        GuideCachePurgeService guideCachePurgeService)
    {
        _logger = logger;
        _subscriptionManager = subscriptionManager;
        _epgParser = epgParser;
        _patternMatcher = patternMatcher;
        _liveTvManager = liveTvManager;
        _sportsScorer = sportsScorer;
        _recordingScheduler = recordingScheduler;
        _guideCachePurgeService = guideCachePurgeService;
    }

    // ==================== SUBSCRIPTIONS ====================

    /// <summary>
    /// Gets all subscriptions.
    /// </summary>
    [HttpGet("Subscriptions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<Subscription>> GetSubscriptions()
    {
        return Ok(_subscriptionManager.GetAll());
    }

    /// <summary>
    /// Gets a subscription by ID.
    /// </summary>
    [HttpGet("Subscriptions/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<Subscription> GetSubscription([FromRoute] string id)
    {
        var subscription = _subscriptionManager.GetById(id);
        if (subscription == null)
        {
            return NotFound();
        }
        return Ok(subscription);
    }

    /// <summary>
    /// Creates a new subscription.
    /// </summary>
    [HttpPost("Subscriptions")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<Subscription> CreateSubscription([FromBody] CreateSubscriptionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("Name is required");
        }

        // New subscriptions go to the end of the list (lowest priority)
        var existingSubs = Plugin.Instance?.Configuration.Subscriptions;
        var nextSortOrder = existingSubs?.Count ?? 0;

        var subscription = new Subscription
        {
            Name = request.Name,
            Type = request.Type,
            MatchPattern = request.MatchPattern ?? request.Name.ToLowerInvariant(),
            ExcludePatterns = request.ExcludePatterns ?? Array.Empty<string>(),
            SortOrder = nextSortOrder,
            IncludeReplays = request.IncludeReplays,
            Enabled = true
        };

        var created = _subscriptionManager.Add(subscription);
        return CreatedAtAction(nameof(GetSubscription), new { id = created.Id }, created);
    }

    /// <summary>
    /// Updates a subscription.
    /// </summary>
    [HttpPut("Subscriptions/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<Subscription> UpdateSubscription(
        [FromRoute] string id,
        [FromBody] Subscription subscription)
    {
        subscription.Id = id;
        var updated = _subscriptionManager.Update(subscription);
        if (updated == null)
        {
            return NotFound();
        }
        return Ok(updated);
    }

    /// <summary>
    /// Deletes a subscription.
    /// </summary>
    [HttpDelete("Subscriptions/{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteSubscription([FromRoute] string id)
    {
        if (!_subscriptionManager.Delete(id))
        {
            return NotFound();
        }
        return NoContent();
    }

    /// <summary>
    /// Enables or disables a subscription.
    /// </summary>
    [HttpPost("Subscriptions/{id}/Toggle")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult ToggleSubscription([FromRoute] string id, [FromQuery] bool enabled)
    {
        if (!_subscriptionManager.SetEnabled(id, enabled))
        {
            return NotFound();
        }
        return Ok();
    }

    /// <summary>
    /// Reorders subscriptions by setting SortOrder based on the submitted list order.
    /// The first ID in the array gets SortOrder 0 (highest priority), etc.
    /// </summary>
    [HttpPost("Subscriptions/Reorder")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult ReorderSubscriptions([FromBody] ReorderRequest request)
    {
        if (request.OrderedIds == null || request.OrderedIds.Length == 0)
        {
            return BadRequest("OrderedIds is required");
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return StatusCode(500, "Plugin not initialized");
        }

        // Build a lookup from ID to new sort order
        var orderMap = new Dictionary<string, int>();
        for (int i = 0; i < request.OrderedIds.Length; i++)
        {
            orderMap[request.OrderedIds[i]] = i;
        }

        // Update SortOrder for each subscription
        foreach (var sub in config.Subscriptions)
        {
            if (orderMap.TryGetValue(sub.Id, out var newOrder))
            {
                sub.SortOrder = newOrder;
            }
        }

        Plugin.Instance!.SaveConfiguration();
        _logger.LogInformation("Reordered {Count} subscriptions", request.OrderedIds.Length);

        return Ok(new { Success = true, Message = $"Reordered {request.OrderedIds.Length} subscriptions" });
    }

    // ==================== PROGRAM PARSING ====================

    /// <summary>
    /// Parses a program title and returns suggested subscriptions.
    /// </summary>
    [HttpPost("Parse")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ParseProgramResponse> ParseProgram([FromBody] ParseProgramRequest request)
    {
        var dto = new BaseItemDto
        {
            Id = request.ProgramId ?? string.Empty,
            Name = request.Title,
            ChannelId = request.ChannelId,
            Genres = request.Genres,
            IsLive = request.IsLive
        };

        var parsed = _epgParser.Parse(dto);
        var suggestions = _epgParser.GetSuggestedSubscriptions(parsed);

        // Check which suggestions are already subscribed
        var suggestionsWithStatus = suggestions.Select(s => new SuggestionWithStatus
        {
            Name = s.Name,
            Type = s.Type,
            SuggestedPattern = s.SuggestedPattern,
            IsSubscribed = _subscriptionManager.IsSubscribed(s.Name, s.Type)
        }).ToList();

        return Ok(new ParseProgramResponse
        {
            Parsed = parsed,
            Suggestions = suggestionsWithStatus
        });
    }

    // ==================== STATUS ====================

    /// <summary>
    /// Gets plugin status and summary.
    /// </summary>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<StatusResponse> GetStatus()
    {
        var config = Plugin.Instance?.Configuration;
        var subscriptions = config?.Subscriptions ?? new List<Subscription>();

        return Ok(new StatusResponse
        {
            TotalSubscriptions = subscriptions.Count,
            EnabledSubscriptions = subscriptions.Count(s => s.Enabled),
            MaxConcurrentRecordings = config?.MaxConcurrentRecordings ?? 2,
            AutoSchedulingEnabled = config?.EnableAutoScheduling ?? true,
            DailyScanTime = config?.DailyScanTime ?? "10:00"
        });
    }

    // ==================== SCHEDULE ====================

    /// <summary>
    /// Gets all upcoming scheduled recordings (currently airing + future).
    /// </summary>
    [HttpGet("Schedule/Upcoming")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<UpcomingRecordingsResponse>> GetUpcomingRecordings()
    {
        var response = new UpcomingRecordingsResponse();
        var config = Plugin.Instance?.Configuration;
        response.MaxConcurrent = config?.MaxConcurrentRecordings ?? 2;

        try
        {
            // Get all scheduled timers from Jellyfin's DVR
            var timers = await _liveTvManager.GetTimers(new TimerQuery(), CancellationToken.None).ConfigureAwait(false);
            
            var now = DateTime.UtcNow;
            var cutoff48h = now.AddHours(48);
            var today = now.Date;
            var tomorrow = today.AddDays(1);

            // Show ALL upcoming timers (not ended yet), sorted by start time.
            // This includes currently-airing games and all future scheduled recordings.
            var upcomingTimers = timers.Items
                .Where(t => t.EndDate >= now)
                .OrderBy(t => t.StartDate)
                .ToList();

            var count48h = 0;

            foreach (var timer in upcomingTimers)
            {
                // Score the program to extract team/league info
                var scored = _sportsScorer.Score(timer.Name ?? "", timer.ChannelName ?? "", timer.Overview ?? "");

                var dto = new UpcomingRecordingDto
                {
                    ProgramId = timer.ProgramId ?? timer.Id ?? "",
                    Title = timer.Name ?? "Unknown",
                    CleanTitle = scored.CleanTitle ?? timer.Name ?? "Unknown",
                    Team1 = scored.Team1,
                    Team2 = scored.Team2,
                    League = scored.DetectedLeague,
                    ChannelName = timer.ChannelName ?? "Unknown",
                    StartTime = timer.StartDate,
                    EndTime = timer.EndDate,
                    Priority = timer.Priority, // Jellyfin timer priority (higher = more important)
                    HasBackupChannels = false, // Could be enhanced to track this
                    IsSkipped = false
                };

                // Try to find matching subscription
                var subscription = FindMatchingSubscriptionForTitle(timer.Name ?? "");
                if (subscription != null)
                {
                    dto.SubscriptionName = subscription.Name;
                    dto.SubscriptionType = subscription.Type.ToString();
                }
                else
                {
                    dto.SubscriptionName = scored.DetectedLeague ?? "Manual";
                    dto.SubscriptionType = "Unknown";
                }

                response.Scheduled.Add(dto);

                // Track 48h count separately
                if (timer.StartDate < cutoff48h)
                {
                    count48h++;
                }

                // Count by date for summary
                var dateKey = timer.StartDate.Date == today ? "Today" :
                              timer.StartDate.Date == tomorrow ? "Tomorrow" :
                              timer.StartDate.ToString("ddd, MMM d");
                
                if (!response.SummaryByDate.ContainsKey(dateKey))
                {
                    response.SummaryByDate[dateKey] = 0;
                }
                response.SummaryByDate[dateKey]++;
            }

            response.TotalScheduled = response.Scheduled.Count;
            response.Upcoming48h = count48h;
            
            // Calculate next daily scan time
            var scanTimeStr = config?.DailyScanTime ?? "10:00";
            var parts = scanTimeStr.Split(':');
            int scanHour = 10, scanMinute = 0;
            if (parts.Length >= 2 && int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m))
            {
                scanHour = Math.Clamp(h, 0, 23);
                scanMinute = Math.Clamp(m, 0, 59);
            }
            var nextScan = now.Date.AddHours(scanHour).AddMinutes(scanMinute);
            if (nextScan <= now) nextScan = nextScan.AddDays(1);
            response.NextScanTime = nextScan;

            _logger.LogInformation("Returning {Count} upcoming recordings ({Count48h} in next 48h)", response.TotalScheduled, count48h);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting upcoming recordings");
        }

        return Ok(response);
    }

    /// <summary>
    /// Finds a matching subscription for a given title.
    /// </summary>
    private Subscription? FindMatchingSubscriptionForTitle(string title)
    {
        var subscriptions = _subscriptionManager.GetAll().Where(s => s.Enabled).ToList();
        var titleLower = title.ToLowerInvariant();

        foreach (var sub in subscriptions.OrderBy(s => s.SortOrder))
        {
            var pattern = sub.MatchPattern?.ToLowerInvariant() ?? sub.Name.ToLowerInvariant();
            if (titleLower.Contains(pattern))
            {
                return sub;
            }
        }

        return null;
    }

    /// <summary>
    /// Triggers an immediate EPG scan and schedules matching recordings.
    /// </summary>
    [HttpPost("Schedule/ScanNow")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ScanNowResponse>> ScanNow()
    {
        _logger.LogInformation("Manual EPG scan triggered via API");
        
        try
        {
            // Trigger the scan and get actual results
            var scanResult = await _recordingScheduler.ScanEpgAsync(CancellationToken.None).ConfigureAwait(false);
            
            // Get total upcoming count from Jellyfin timers (including non-sports)
            var timers = await _liveTvManager.GetTimers(new TimerQuery(), CancellationToken.None).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            var cutoff = now.AddHours(48);
            var totalUpcoming = timers.Items
                .Count(t => t.EndDate >= now && t.StartDate < cutoff);
            
            return Ok(new ScanNowResponse
            {
                Success = true,
                Message = "EPG scan completed successfully",
                MatchesFound = scanResult.MatchesFound,
                NewRecordings = scanResult.NewRecordings,
                AlreadyScheduled = scanResult.AlreadyScheduled,
                TotalUpcoming = totalUpcoming
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual EPG scan failed");
            return Ok(new ScanNowResponse
            {
                Success = false,
                Message = $"Scan failed: {ex.Message}",
                MatchesFound = 0,
                NewRecordings = 0,
                AlreadyScheduled = 0,
                TotalUpcoming = 0
            });
        }
    }

    /// <summary>
    /// Cancels all Sports DVR timers from Jellyfin's DVR schedule.
    /// This only removes timers that were created by SportsDVR subscriptions.
    /// </summary>
    [HttpPost("Schedule/ClearTimers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ClearTimers()
    {
        _logger.LogInformation("Clear timers triggered via API");

        try
        {
            var cancelled = await _recordingScheduler.CancelSportsDvrTimersAsync(CancellationToken.None).ConfigureAwait(false);
            return Ok(new
            {
                Success = true,
                Message = $"Cancelled {cancelled} Sports DVR timer(s)",
                CancelledCount = cancelled
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear timers");
            return Ok(new
            {
                Success = false,
                Message = $"Failed: {ex.Message}",
                CancelledCount = 0
            });
        }
    }

    /// <summary>
    /// Cancels ALL scheduled timers from Jellyfin's DVR — every single one, not just Sports DVR.
    /// Use this as a nuclear option to clean up orphaned timers from old plugin versions.
    /// </summary>
    [HttpPost("Schedule/ClearAllTimers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ClearAllTimers()
    {
        _logger.LogWarning("NUCLEAR clear ALL timers triggered via API");

        try
        {
            var cancelled = await _recordingScheduler.CancelAllTimersAsync(CancellationToken.None).ConfigureAwait(false);
            return Ok(new
            {
                Success = true,
                Message = $"Cancelled ALL {cancelled} timer(s) from Jellyfin DVR",
                CancelledCount = cancelled
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear all timers");
            return Ok(new
            {
                Success = false,
                Message = $"Failed: {ex.Message}",
                CancelledCount = 0
            });
        }
    }

    /// <summary>
    /// Clears the plugin's internal schedule tracking cache.
    /// Use after changing tuner or EPG source.
    /// </summary>
    [HttpPost("Schedule/ClearCache")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ClearCacheResponse> ClearScheduleCache()
    {
        _logger.LogInformation("Schedule cache clear triggered via API");
        
        try
        {
            // Clear the internal tracking set in RecordingScheduler
            _recordingScheduler.ClearScheduledProgramsCache();
            
            return Ok(new ClearCacheResponse
            {
                Success = true,
                Message = "Schedule cache cleared. Run 'Scan EPG Now' to rescan."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear schedule cache");
            return Ok(new ClearCacheResponse
            {
                Success = false,
                Message = $"Clear failed: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Purges Jellyfin's Live TV guide cache and triggers a guide refresh.
    /// Clears stale EPG data so the guide loads fresh programs (fixes "Game Complete" entries).
    /// </summary>
    [HttpPost("Schedule/PurgeGuideCache")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult PurgeGuideCache()
    {
        _logger.LogInformation("Manual guide cache purge triggered via API");

        try
        {
            var result = _guideCachePurgeService.PurgeNow();
            return Ok(new
            {
                result.Success,
                result.Message,
                result.FilesDeleted,
                result.DirsDeleted,
                LastPurge = _guideCachePurgeService.GetLastPurgeTime()?.ToString("o")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Guide cache purge failed");
            return Ok(new
            {
                Success = false,
                Message = $"Purge failed: {ex.Message}",
                FilesDeleted = 0,
                DirsDeleted = 0
            });
        }
    }

    // ==================== EPG ANALYSIS ====================

    /// <summary>
    /// Scores a single program title to determine if it's a sports game.
    /// Useful for testing matching logic.
    /// </summary>
    [HttpPost("Analysis/ScoreTitle")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ScoreTitleResponse> ScoreTitle([FromBody] ScoreTitleRequest request)
    {
        var result = _sportsScorer.Score(
            request.Title, 
            request.Channel, 
            request.Description, 
            request.HasSportsCategory);

        return Ok(new ScoreTitleResponse
        {
            Title = request.Title,
            Score = result.Score,
            IsLikelyGame = result.IsLikelyGame,
            IsPossibleGame = result.IsPossibleGame,
            CleanTitle = result.CleanTitle,
            Team1 = result.Team1,
            Team2 = result.Team2,
            DetectedLeague = result.DetectedLeague,
            IsReplay = result.IsReplay,
            HasMatchupPattern = result.HasMatchupPattern,
            IsSportsChannel = result.IsSportsChannel,
            IsPregamePostgame = result.IsPregamePostgame
        });
    }

    /// <summary>
    /// Analyzes the current EPG and returns statistics on sports detection.
    /// </summary>
    [HttpGet("Analysis/EpgStats")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<EpgAnalysisResponse>> AnalyzeEpg(
        [FromQuery] int minScore = 30,
        [FromQuery] int hours = 48)
    {
        var response = new EpgAnalysisResponse();

        try
        {
            // Get channels using the correct API
            var channelQuery = new LiveTvChannelQuery { };
            var dtoOptions = new DtoOptions();
            var channels = _liveTvManager.GetInternalChannels(channelQuery, dtoOptions, CancellationToken.None);

            var channelMap = channels.Items
                .ToDictionary(c => c.Id.ToString("N"), c => c.Name ?? "Unknown");

            // Get programs for each channel
            var now = DateTime.UtcNow;
            var endDate = now.AddHours(hours);
            var scoredResults = new List<ScoredProgramResult>();
            var leagueCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var scoreDistribution = new Dictionary<string, int>
            {
                ["< 0 (Not sports)"] = 0,
                ["0-29 (Unlikely)"] = 0,
                ["30-49 (Possible)"] = 0,
                ["50-69 (Probable)"] = 0,
                ["70+ (Definite)"] = 0
            };
            int totalPrograms = 0;

            foreach (var channel in channels.Items)
            {
                try
                {
                    var programQuery = new InternalItemsQuery
                    {
                        ChannelIds = new[] { channel.Id },
                        MinStartDate = now,
                        MaxStartDate = endDate
                    };

                    var programs = await _liveTvManager.GetPrograms(programQuery, dtoOptions, CancellationToken.None).ConfigureAwait(false);
                    totalPrograms += programs.Items.Count;

                    foreach (var program in programs.Items)
                    {
                        var title = program.Name ?? string.Empty;
                        var channelName = channel.Name ?? "Unknown";
                        var episodeTitle = program.EpisodeTitle ?? string.Empty;
                        var desc = string.IsNullOrEmpty(episodeTitle) ? program.Overview : $"{episodeTitle} - {program.Overview}";
                        
                        var hasSportsCat = program.IsSports ?? false;

                        var scored = _sportsScorer.Score(title, channelName, desc, hasSportsCat);

                        // Score distribution
                        if (scored.Score < 0)
                            scoreDistribution["< 0 (Not sports)"]++;
                        else if (scored.Score < 30)
                            scoreDistribution["0-29 (Unlikely)"]++;
                        else if (scored.Score < 50)
                            scoreDistribution["30-49 (Possible)"]++;
                        else if (scored.Score < 70)
                            scoreDistribution["50-69 (Probable)"]++;
                        else
                            scoreDistribution["70+ (Definite)"]++;

                        // Track league counts for likely games
                        if (scored.IsLikelyGame && !string.IsNullOrEmpty(scored.DetectedLeague))
                        {
                            leagueCounts.TryGetValue(scored.DetectedLeague, out var count);
                            leagueCounts[scored.DetectedLeague] = count + 1;
                        }

                        // Add to results if meets threshold
                        if (scored.Score >= minScore)
                        {
                            scoredResults.Add(new ScoredProgramResult
                            {
                                Title = title,
                                CleanTitle = scored.CleanTitle,
                                Channel = channelName,
                                Score = scored.Score,
                                IsLikelyGame = scored.IsLikelyGame,
                                Team1 = scored.Team1,
                                Team2 = scored.Team2,
                                League = scored.DetectedLeague,
                                IsReplay = scored.IsReplay,
                                StartTime = program.StartDate ?? now
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get programs for channel {Channel}", channel.Name);
                }
            }

            response.TotalPrograms = totalPrograms;
            response.LikelyGamesCount = scoredResults.Count(s => s.IsLikelyGame);
            response.PossibleGamesCount = scoredResults.Count(s => s.Score >= 30);
            response.ScoreDistribution = scoreDistribution;
            response.LeagueCounts = leagueCounts.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
            response.TopScoredPrograms = scoredResults.OrderByDescending(s => s.Score).Take(50).ToList();
            response.HoursScanned = hours;
            response.MinScoreThreshold = minScore;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to analyze EPG");
            response.Error = ex.Message;
        }

        return Ok(response);
    }

    /// <summary>
    /// Tests subscription matching against current EPG data.
    /// </summary>
    [HttpGet("Analysis/TestSubscriptions")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<SubscriptionTestResponse>> TestSubscriptions([FromQuery] int hours = 48)
    {
        var response = new SubscriptionTestResponse();

        try
        {
            var subscriptions = _subscriptionManager.GetAll().Where(s => s.Enabled).ToList();
            response.ActiveSubscriptions = subscriptions.Count;

            // Get channels using correct API
            var channelQuery = new LiveTvChannelQuery { };
            var dtoOptions = new DtoOptions();
            var channels = _liveTvManager.GetInternalChannels(channelQuery, dtoOptions, CancellationToken.None);

            var matchResults = new List<SubscriptionMatchResult>();
            var now = DateTime.UtcNow;
            var endDate = now.AddHours(hours);

            // Build programs list from all channels
            var allPrograms = new List<(string Title, string ChannelName, string EpisodeTitle)>();
            
            foreach (var channel in channels.Items)
            {
                try
                {
                    var programQuery = new InternalItemsQuery
                    {
                        ChannelIds = new[] { channel.Id },
                        MinStartDate = now,
                        MaxStartDate = endDate
                    };

                    var programs = await _liveTvManager.GetPrograms(programQuery, dtoOptions, CancellationToken.None).ConfigureAwait(false);
                    
                    foreach (var program in programs.Items)
                    {
                        allPrograms.Add((
                            program.Name ?? string.Empty,
                            channel.Name ?? "Unknown",
                            program.EpisodeTitle ?? string.Empty
                        ));
                    }
                }
                catch
                {
                    // Skip failed channels
                }
            }

            foreach (var sub in subscriptions)
            {
                var matchCount = 0;
                var sampleMatches = new List<string>();

                // Build regex from pattern for testing
                var patternRegex = BuildPatternRegex(sub.MatchPattern);

                foreach (var (title, channelName, episodeTitle) in allPrograms)
                {
                    // Combine all searchable text
                    var searchText = $"{title} {episodeTitle} {channelName}".ToLowerInvariant();

                    // Test pattern match
                    if (patternRegex.IsMatch(searchText))
                    {
                        matchCount++;
                        if (sampleMatches.Count < 5)
                        {
                            sampleMatches.Add($"{title} [{channelName}]");
                        }
                    }
                }

                matchResults.Add(new SubscriptionMatchResult
                {
                    SubscriptionName = sub.Name,
                    Pattern = sub.MatchPattern,
                    Type = sub.Type.ToString(),
                    MatchCount = matchCount,
                    SampleMatches = sampleMatches
                });
            }

            response.Results = matchResults.OrderByDescending(r => r.MatchCount).ToList();
            response.TotalMatches = matchResults.Sum(r => r.MatchCount);
            response.HoursScanned = hours;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test subscriptions");
            response.Error = ex.Message;
        }

        return Ok(response);
    }

    /// <summary>
    /// Test a single subscription against the current EPG. Used by the config UI "Test Match" button.
    /// Returns matching programs with score, channel, and time information.
    /// </summary>
    [HttpPost("Subscriptions/Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> TestSingleSubscription([FromBody] TestSubscriptionRequest request)
    {
        try
        {
            var channelQuery = new LiveTvChannelQuery { };
            var dtoOptions = new DtoOptions(true);
            var channels = _liveTvManager.GetInternalChannels(channelQuery, dtoOptions, CancellationToken.None);
            var now = DateTime.UtcNow;
            var endDate = now.AddHours(72);
            var matches = new List<object>();
            var pattern = (request.MatchPattern ?? request.Name ?? "").ToLowerInvariant();

            foreach (var channel in channels.Items)
            {
                try
                {
                    var programQuery = new InternalItemsQuery
                    {
                        ChannelIds = new[] { channel.Id },
                        MinStartDate = now,
                        MaxStartDate = endDate
                    };

                    var programs = await _liveTvManager.GetPrograms(programQuery, dtoOptions, CancellationToken.None).ConfigureAwait(false);
                    
                    foreach (var program in programs.Items)
                    {
                        var title = program.Name ?? "";
                        var episodeTitle = program.EpisodeTitle ?? "";
                        var genres = program.Genres ?? Array.Empty<string>();
                        var desc = program.Overview ?? "";
                        var searchText = $"{title} {episodeTitle} {desc} {channel.Name} {string.Join(" ", genres)}".ToLowerInvariant();
                        
                        if (searchText.Contains(pattern))
                        {
                            // Score it
                            var hasSportsCategory = (program.IsSports ?? false) ||
                                genres.Any(g => g.Contains("Sports", StringComparison.OrdinalIgnoreCase) ||
                                    g.Contains("Soccer", StringComparison.OrdinalIgnoreCase) ||
                                    g.Contains("Basketball", StringComparison.OrdinalIgnoreCase) ||
                                    g.Contains("Football", StringComparison.OrdinalIgnoreCase) ||
                                    g.Contains("Hockey", StringComparison.OrdinalIgnoreCase) ||
                                    g.Contains("Baseball", StringComparison.OrdinalIgnoreCase));
                            var descWithEpisode = string.Join(" ", new[] { episodeTitle, desc }.Where(s => !string.IsNullOrEmpty(s)));
                            var scored = _sportsScorer.Score(title, channel.Name, descWithEpisode, hasSportsCategory);
                            
                            matches.Add(new
                            {
                                Name = title,
                                EpisodeTitle = episodeTitle,
                                ChannelName = channel.Name ?? "Unknown",
                                StartDate = program.StartDate,
                                EndDate = program.EndDate,
                                Score = scored.Score,
                                Genres = genres,
                                IsLive = program.IsLive ?? false,
                                DetectedLeague = scored.DetectedLeague
                            });
                        }
                    }
                }
                catch { /* skip failed channels */ }
            }

            // Sort by start date, limit to 50 results
            var sortedMatches = matches
                .OrderBy(m => ((dynamic)m).StartDate ?? DateTime.MaxValue)
                .Take(50)
                .ToList();

            return Ok(new { Matches = sortedMatches, Pattern = pattern });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test subscription");
            return Ok(new { Matches = Array.Empty<object>(), Error = ex.Message });
        }
    }

    /// <summary>
    /// Builds a regex from a pattern (handles simple patterns and regex).
    /// </summary>
    private static Regex BuildPatternRegex(string pattern)
    {
        try
        {
            // If it looks like a regex pattern, use it directly
            if (pattern.Contains("|") || pattern.Contains("(") || pattern.Contains("["))
            {
                return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            
            // Otherwise treat as a literal search term
            return new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
        catch
        {
            // If regex fails, fall back to literal
            return new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }

    // ==================== ALIASES ====================

    /// <summary>
    /// Gets all known aliases for a team name.
    /// </summary>
    [HttpGet("Aliases/{teamName}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<AliasResponse> GetAliases([FromRoute] string teamName)
    {
        var aliases = TeamAliases.GetAliases(teamName).ToList();
        var canonical = TeamAliases.GetCanonicalName(teamName);

        return Ok(new AliasResponse
        {
            OriginalName = teamName,
            CanonicalName = canonical,
            Aliases = aliases,
            SuggestedPattern = TeamAliases.BuildMatchPattern(teamName)
        });
    }

    /// <summary>
    /// Checks if two team names are equivalent (via aliases).
    /// </summary>
    [HttpGet("Aliases/Check")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<AliasCheckResponse> CheckAlias(
        [FromQuery] string name1,
        [FromQuery] string name2)
    {
        return Ok(new AliasCheckResponse
        {
            Name1 = name1,
            Name2 = name2,
            AreEquivalent = TeamAliases.AreEquivalent(name1, name2),
            Canonical1 = TeamAliases.GetCanonicalName(name1),
            Canonical2 = TeamAliases.GetCanonicalName(name2)
        });
    }

    /// <summary>
    /// Gets all custom aliases.
    /// </summary>
    [HttpGet("Aliases/Custom")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<CustomTeamAlias>> GetCustomAliases()
    {
        return Ok(Plugin.Instance?.Configuration.CustomAliases ?? new List<CustomTeamAlias>());
    }

    /// <summary>
    /// Adds a custom alias.
    /// </summary>
    [HttpPost("Aliases/Custom")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public ActionResult<CustomTeamAlias> AddCustomAlias([FromBody] CustomTeamAlias alias)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return StatusCode(500, "Plugin not initialized");
        }

        config.CustomAliases.Add(alias);
        Plugin.Instance!.SaveConfiguration();

        return CreatedAtAction(nameof(GetCustomAliases), alias);
    }

    /// <summary>
    /// Deletes a custom alias.
    /// </summary>
    [HttpDelete("Aliases/Custom/{canonicalName}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteCustomAlias([FromRoute] string canonicalName)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return StatusCode(500, "Plugin not initialized");
        }

        var alias = config.CustomAliases.FirstOrDefault(
            a => a.CanonicalName.Equals(canonicalName, StringComparison.OrdinalIgnoreCase));

        if (alias == null)
        {
            return NotFound();
        }

        config.CustomAliases.Remove(alias);
        Plugin.Instance!.SaveConfiguration();

        return NoContent();
    }
}

// ==================== REQUEST/RESPONSE MODELS ====================

/// <summary>
/// Request to reorder subscriptions.
/// </summary>
public class ReorderRequest
{
    /// <summary>
    /// Gets or sets the ordered subscription IDs. Index 0 = highest priority.
    /// </summary>
    [Required]
    public string[] OrderedIds { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Request to create a subscription.
/// </summary>
public class CreateSubscriptionRequest
{
    /// <summary>
    /// Gets or sets the name (required).
    /// </summary>
    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the subscription type.
    /// </summary>
    public SubscriptionType Type { get; set; } = SubscriptionType.Team;

    /// <summary>
    /// Gets or sets the match pattern (defaults to name).
    /// </summary>
    public string? MatchPattern { get; set; }

    /// <summary>
    /// Gets or sets exclusion patterns.
    /// </summary>
    public string[]? ExcludePatterns { get; set; }

    /// <summary>
    /// Gets or sets whether to include replays.
    /// </summary>
    public bool IncludeReplays { get; set; }
}

/// <summary>
/// Request to parse a program.
/// </summary>
public class ParseProgramRequest
{
    /// <summary>
    /// Gets or sets the program title.
    /// </summary>
    [Required]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the program ID.
    /// </summary>
    public string? ProgramId { get; set; }

    /// <summary>
    /// Gets or sets the channel ID.
    /// </summary>
    public string? ChannelId { get; set; }

    /// <summary>
    /// Gets or sets the genres.
    /// </summary>
    public string[]? Genres { get; set; }

    /// <summary>
    /// Gets or sets whether this is live.
    /// </summary>
    public bool? IsLive { get; set; }
}

/// <summary>
/// Response from parsing a program.
/// </summary>
public class ParseProgramResponse
{
    /// <summary>
    /// Gets or sets the parsed program data.
    /// </summary>
    public ParsedProgram Parsed { get; set; } = new();

    /// <summary>
    /// Gets or sets suggested subscriptions.
    /// </summary>
    public List<SuggestionWithStatus> Suggestions { get; set; } = new();
}

/// <summary>
/// A subscription suggestion with subscription status.
/// </summary>
public class SuggestionWithStatus
{
    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the type.
    /// </summary>
    public SubscriptionType Type { get; set; }

    /// <summary>
    /// Gets or sets the suggested pattern.
    /// </summary>
    public string SuggestedPattern { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether already subscribed.
    /// </summary>
    public bool IsSubscribed { get; set; }
}

/// <summary>
/// Plugin status response.
/// </summary>
public class StatusResponse
{
    /// <summary>
    /// Gets or sets total subscription count.
    /// </summary>
    public int TotalSubscriptions { get; set; }

    /// <summary>
    /// Gets or sets enabled subscription count.
    /// </summary>
    public int EnabledSubscriptions { get; set; }

    /// <summary>
    /// Gets or sets max concurrent recordings.
    /// </summary>
    public int MaxConcurrentRecordings { get; set; }

    /// <summary>
    /// Gets or sets whether auto-scheduling is enabled.
    /// </summary>
    public bool AutoSchedulingEnabled { get; set; }

    /// <summary>
    /// Gets or sets the daily EPG scan time.
    /// </summary>
    public string DailyScanTime { get; set; } = "10:00";
}

/// <summary>
/// Response containing alias information for a team.
/// </summary>
public class AliasResponse
{
    /// <summary>
    /// Gets or sets the original name queried.
    /// </summary>
    public string OriginalName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the canonical (official) team name.
    /// </summary>
    public string CanonicalName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets all known aliases for this team.
    /// </summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>
    /// Gets or sets a suggested regex pattern to match all aliases.
    /// </summary>
    public string SuggestedPattern { get; set; } = string.Empty;
}

/// <summary>
/// Response for alias equivalence check.
/// </summary>
public class AliasCheckResponse
{
    /// <summary>
    /// Gets or sets the first name.
    /// </summary>
    public string Name1 { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the second name.
    /// </summary>
    public string Name2 { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether the names are equivalent.
    /// </summary>
    public bool AreEquivalent { get; set; }

    /// <summary>
    /// Gets or sets the canonical name for the first name.
    /// </summary>
    public string Canonical1 { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the canonical name for the second name.
    /// </summary>
    public string Canonical2 { get; set; } = string.Empty;
}

// ==================== EPG ANALYSIS DTOS ====================

/// <summary>
/// Request to score a single title.
/// </summary>
public class ScoreTitleRequest
{
    /// <summary>Program title to score.</summary>
    public string Title { get; set; } = string.Empty;
    
    /// <summary>Optional channel name.</summary>
    public string? Channel { get; set; }
    
    /// <summary>Optional description.</summary>
    public string? Description { get; set; }
    
    /// <summary>Whether the program has a sports category.</summary>
    public bool HasSportsCategory { get; set; }
}

/// <summary>
/// Response from scoring a title.
/// </summary>
public class ScoreTitleResponse
{
    /// <summary>Original title.</summary>
    public string Title { get; set; } = string.Empty;
    
    /// <summary>Confidence score.</summary>
    public int Score { get; set; }
    
    /// <summary>Whether this is likely a game (score >= 50).</summary>
    public bool IsLikelyGame { get; set; }
    
    /// <summary>Whether this is possibly a game (score >= 30).</summary>
    public bool IsPossibleGame { get; set; }
    
    /// <summary>Cleaned title.</summary>
    public string? CleanTitle { get; set; }
    
    /// <summary>Detected team 1.</summary>
    public string? Team1 { get; set; }
    
    /// <summary>Detected team 2.</summary>
    public string? Team2 { get; set; }
    
    /// <summary>Detected league.</summary>
    public string? DetectedLeague { get; set; }
    
    /// <summary>Whether this is a replay.</summary>
    public bool IsReplay { get; set; }
    
    /// <summary>Whether a matchup pattern was found.</summary>
    public bool HasMatchupPattern { get; set; }
    
    /// <summary>Whether the channel is a sports channel.</summary>
    public bool IsSportsChannel { get; set; }
    
    /// <summary>Whether this is pregame/postgame content.</summary>
    public bool IsPregamePostgame { get; set; }
}

/// <summary>
/// Response from EPG analysis.
/// </summary>
public class EpgAnalysisResponse
{
    /// <summary>Total programs scanned.</summary>
    public int TotalPrograms { get; set; }
    
    /// <summary>Number of likely games found.</summary>
    public int LikelyGamesCount { get; set; }
    
    /// <summary>Number of possible games found.</summary>
    public int PossibleGamesCount { get; set; }
    
    /// <summary>Hours of EPG data scanned.</summary>
    public int HoursScanned { get; set; }
    
    /// <summary>Minimum score threshold used.</summary>
    public int MinScoreThreshold { get; set; }
    
    /// <summary>Score distribution buckets.</summary>
    public Dictionary<string, int> ScoreDistribution { get; set; } = new();
    
    /// <summary>Counts by detected league.</summary>
    public Dictionary<string, int> LeagueCounts { get; set; } = new();
    
    /// <summary>Top scored programs.</summary>
    public List<ScoredProgramResult> TopScoredPrograms { get; set; } = new();
    
    /// <summary>Error message if any.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// A scored program result.
/// </summary>
public class ScoredProgramResult
{
    /// <summary>Original title.</summary>
    public string Title { get; set; } = string.Empty;
    
    /// <summary>Cleaned title.</summary>
    public string? CleanTitle { get; set; }
    
    /// <summary>Channel name.</summary>
    public string? Channel { get; set; }
    
    /// <summary>Confidence score.</summary>
    public int Score { get; set; }
    
    /// <summary>Whether likely a game.</summary>
    public bool IsLikelyGame { get; set; }
    
    /// <summary>Detected team 1.</summary>
    public string? Team1 { get; set; }
    
    /// <summary>Detected team 2.</summary>
    public string? Team2 { get; set; }
    
    /// <summary>Detected league.</summary>
    public string? League { get; set; }
    
    /// <summary>Whether a replay.</summary>
    public bool IsReplay { get; set; }
    
    /// <summary>Program start time.</summary>
    public DateTime StartTime { get; set; }
}

/// <summary>
/// Response from subscription testing.
/// </summary>
public class SubscriptionTestResponse
{
    /// <summary>Number of active subscriptions.</summary>
    public int ActiveSubscriptions { get; set; }
    
    /// <summary>Total matches across all subscriptions.</summary>
    public int TotalMatches { get; set; }
    
    /// <summary>Hours of EPG data scanned.</summary>
    public int HoursScanned { get; set; }
    
    /// <summary>Match results by subscription.</summary>
    public List<SubscriptionMatchResult> Results { get; set; } = new();
    
    /// <summary>Error message if any.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Result of testing a single subscription.
/// </summary>
public class SubscriptionMatchResult
{
    /// <summary>Subscription name.</summary>
    public string SubscriptionName { get; set; } = string.Empty;
    
    /// <summary>Match pattern.</summary>
    public string Pattern { get; set; } = string.Empty;
    
    /// <summary>Subscription type.</summary>
    public string Type { get; set; } = string.Empty;
    
    /// <summary>Number of matches found.</summary>
    public int MatchCount { get; set; }
    
    /// <summary>Sample matching titles.</summary>
    public List<string> SampleMatches { get; set; } = new();
}

/// <summary>
/// Request for testing a single subscription against the EPG.
/// </summary>
public class TestSubscriptionRequest
{
    /// <summary>Subscription name.</summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>Subscription type (0=Team, 1=League, 2=Event).</summary>
    public int Type { get; set; }
    
    /// <summary>Match pattern (defaults to Name if empty).</summary>
    public string? MatchPattern { get; set; }
}
