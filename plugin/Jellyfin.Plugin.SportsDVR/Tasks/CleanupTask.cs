using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SportsDVR.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsDVR.Tasks;

/// <summary>
/// Scheduled task to clean up old sports recordings.
/// </summary>
public class CleanupTask : IScheduledTask
{
    private readonly ILogger<CleanupTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CleanupTask"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{CleanupTask}"/> interface.</param>
    public CleanupTask(ILogger<CleanupTask> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Clean Up Old Sports Recordings";

    /// <inheritdoc />
    public string Key => "SportsDVRCleanup";

    /// <inheritdoc />
    public string Description => "Deletes sports recordings older than the configured retention period.";

    /// <inheritdoc />
    public string Category => "Sports DVR";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks // Run at 4 AM
            }
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            _logger.LogWarning("Plugin configuration not available");
            return;
        }

        var libraryPath = config.SportsLibraryPath;
        var retentionDays = config.RetentionDays;

        if (string.IsNullOrEmpty(libraryPath) || !Directory.Exists(libraryPath))
        {
            _logger.LogWarning("Sports library path not configured or does not exist: {Path}", libraryPath);
            return;
        }

        var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
        _logger.LogInformation("Cleaning up recordings older than {CutoffDate}", cutoffDate);

        var files = Directory.GetFiles(libraryPath, "*.*", SearchOption.AllDirectories)
            .Where(f => File.GetCreationTimeUtc(f) < cutoffDate)
            .ToList();

        _logger.LogInformation("Found {Count} files to delete", files.Count);

        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            try
            {
                File.Delete(files[i]);
                _logger.LogDebug("Deleted: {File}", files[i]);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete file: {File}", files[i]);
            }

            progress.Report((double)(i + 1) / files.Count * 100);
        }

        _logger.LogInformation("Cleanup complete. Deleted {Count} files.", files.Count);
        await Task.CompletedTask;
    }
}
