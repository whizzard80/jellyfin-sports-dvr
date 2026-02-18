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
/// Runs once daily at the configured time (default 10:00 AM server local time)
/// and once at startup. Builds the full optimized schedule for the next 24 hours.
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
    public string Description => "Scans the full EPG once daily, builds an optimized recording schedule for the next 24 hours using subscription priorities.";

    /// <inheritdoc />
    public string Category => "Sports DVR";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Parse the configured daily scan time (e.g. "10:00")
        var config = Plugin.Instance?.Configuration;
        var scanTimeStr = config?.DailyScanTime ?? "10:00";

        int scanHour = 10;
        int scanMinute = 0;
        var parts = scanTimeStr.Split(':');
        if (parts.Length >= 2 &&
            int.TryParse(parts[0], out var h) &&
            int.TryParse(parts[1], out var m))
        {
            scanHour = Math.Clamp(h, 0, 23);
            scanMinute = Math.Clamp(m, 0, 59);
        }

        return new[]
        {
            // Scan at startup so the schedule is built after a restart
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.StartupTrigger
            },
            // Daily scan at the configured time
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = new TimeSpan(scanHour, scanMinute, 0).Ticks
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Sports DVR daily EPG scan starting");
        progress.Report(0);

        try
        {
            _guideCachePurgeService.PurgeIfNeeded();
            progress.Report(10);
            var result = await _recordingScheduler.ScanEpgAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(100);

            _logger.LogInformation(
                "Sports DVR daily EPG scan complete: {Matches} matches, {New} scheduled, {Existing} already scheduled",
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
