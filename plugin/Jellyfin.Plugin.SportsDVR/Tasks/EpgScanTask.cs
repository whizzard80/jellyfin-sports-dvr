using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SportsDVR.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsDVR.Tasks;

/// <summary>
/// Jellyfin scheduled task that scans the EPG for matching sports programs
/// and schedules recordings via the DVR.
/// Appears in Jellyfin Dashboard → Scheduled Tasks.
/// </summary>
public class EpgScanTask : IScheduledTask
{
    private readonly ILogger<EpgScanTask> _logger;
    private readonly RecordingScheduler _recordingScheduler;
    private readonly GuideCachePurgeService _guideCachePurgeService;

    /// <summary>
    /// Initializes a new instance of the <see cref="EpgScanTask"/> class.
    /// </summary>
    public EpgScanTask(
        ILogger<EpgScanTask> logger,
        RecordingScheduler recordingScheduler,
        GuideCachePurgeService guideCachePurgeService)
    {
        _logger = logger;
        _recordingScheduler = recordingScheduler;
        _guideCachePurgeService = guideCachePurgeService;
    }

    /// <inheritdoc />
    public string Name => "Scan EPG for Sports";

    /// <inheritdoc />
    public string Key => "SportsDVREpgScan";

    /// <inheritdoc />
    public string Description => "Scans the Electronic Program Guide for programs matching Sports DVR subscriptions and schedules recordings.";

    /// <inheritdoc />
    public string Category => "Sports DVR";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            // Run on startup (with a brief delay handled by Jellyfin)
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.StartupTrigger
            },

            // Run every 30 minutes
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(30).Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Sports DVR EPG scan task starting");
        progress.Report(0);

        try
        {
            _guideCachePurgeService.PurgeIfNeeded();
            progress.Report(10);
            var result = await _recordingScheduler.ScanEpgAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);

            _logger.LogInformation(
                "Sports DVR EPG scan task complete: {Matches} matches, {New} new, {Existing} already scheduled",
                result.MatchesFound,
                result.NewRecordings,
                result.AlreadyScheduled);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Sports DVR EPG scan task was cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sports DVR EPG scan task failed");
            throw;
        }
    }
}
