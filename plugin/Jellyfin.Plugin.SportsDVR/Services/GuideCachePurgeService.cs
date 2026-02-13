using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SportsDVR.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsDVR.Services;

/// <summary>
/// Purges Jellyfin's Live TV guide cache and triggers a guide refresh.
/// Works around Jellyfin not invalidating cache when "Refresh Guide Data" runs (issue #6103).
/// </summary>
public class GuideCachePurgeService
{
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(24);
    private const string LastPurgeFileName = "SportsDVR_last_guide_purge.txt";

    private readonly ILogger<GuideCachePurgeService> _logger;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ITaskManager _taskManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="GuideCachePurgeService"/> class.
    /// </summary>
    public GuideCachePurgeService(
        ILogger<GuideCachePurgeService> logger,
        IApplicationPaths applicationPaths,
        ITaskManager taskManager)
    {
        _logger = logger;
        _applicationPaths = applicationPaths;
        _taskManager = taskManager;
    }

    /// <summary>
    /// If enabled in config and conditions are met (correct hour, 24h cooldown), purges the guide cache.
    /// Called automatically at the start of the EPG scan task.
    /// </summary>
    public void PurgeIfNeeded()
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnableDailyGuideCachePurge)
        {
            return;
        }

        var purgeHour = Math.Clamp(config.GuideCachePurgeHour, 0, 23);
        if (DateTime.Now.Hour != purgeHour)
        {
            return;
        }

        var cachePath = _applicationPaths.CachePath;
        if (string.IsNullOrEmpty(cachePath) || !Directory.Exists(cachePath))
        {
            return;
        }

        if (!IsPurgeDue(cachePath))
        {
            return;
        }

        _logger.LogInformation("Daily guide cache purge starting (hour {Hour}:00)", purgeHour);
        var result = PurgeGuideCache(cachePath);
        RecordPurgeTime(cachePath);

        if (result.FilesDeleted > 0 || result.DirsDeleted > 0)
        {
            TriggerGuideRefreshTask();
        }
    }

    /// <summary>
    /// Immediately purges the guide cache and triggers a guide refresh.
    /// Called from the API button — ignores hour/cooldown.
    /// </summary>
    /// <returns>A summary of what was purged.</returns>
    public PurgeResult PurgeNow()
    {
        var cachePath = _applicationPaths.CachePath;
        if (string.IsNullOrEmpty(cachePath) || !Directory.Exists(cachePath))
        {
            _logger.LogWarning("Cannot purge: cache path not available");
            return new PurgeResult { Success = false, Message = "Cache path not available." };
        }

        _logger.LogInformation("Manual guide cache purge triggered");
        var result = PurgeGuideCache(cachePath);
        RecordPurgeTime(cachePath);
        TriggerGuideRefreshTask();

        result.Success = true;
        result.Message = result.FilesDeleted > 0 || result.DirsDeleted > 0
            ? $"Purged {result.FilesDeleted} xmltv files, {result.DirsDeleted} channel dirs. Guide refresh started."
            : "Cache was already clean. Guide refresh started.";

        return result;
    }

    /// <summary>
    /// Gets the time of the last purge, or null if never purged.
    /// </summary>
    public DateTime? GetLastPurgeTime()
    {
        var cachePath = _applicationPaths.CachePath;
        if (string.IsNullOrEmpty(cachePath)) return null;

        var path = Path.Combine(cachePath, LastPurgeFileName);
        if (!File.Exists(path)) return null;

        try
        {
            var line = File.ReadAllText(path).Trim();
            if (long.TryParse(line, out var ticks))
            {
                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }
        catch { /* ignore */ }

        return null;
    }

    private bool IsPurgeDue(string cachePath)
    {
        var lastPurgePath = Path.Combine(cachePath, LastPurgeFileName);
        if (!File.Exists(lastPurgePath)) return true;

        try
        {
            var line = File.ReadAllText(lastPurgePath).Trim();
            if (long.TryParse(line, out var ticks))
            {
                var lastPurge = new DateTime(ticks, DateTimeKind.Utc);
                return DateTime.UtcNow - lastPurge >= PurgeInterval;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read last purge time, will purge");
        }

        return true;
    }

    private PurgeResult PurgeGuideCache(string cachePath)
    {
        var result = new PurgeResult();

        // Clear xmltv cache files
        var xmltvDir = Path.Combine(cachePath, "xmltv");
        if (Directory.Exists(xmltvDir))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(xmltvDir))
                {
                    try
                    {
                        File.Delete(file);
                        result.FilesDeleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not delete {File}", file);
                    }
                }

                if (result.FilesDeleted > 0)
                {
                    _logger.LogInformation("Guide cache purge: cleared {Count} files in xmltv", result.FilesDeleted);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not clear xmltv cache");
            }
        }

        // Clear *_channels directories
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(cachePath, "*_channels"))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    result.DirsDeleted++;
                    _logger.LogInformation("Guide cache purge: removed {Dir}", Path.GetFileName(dir));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not delete {Dir}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear *_channels cache");
        }

        return result;
    }

    private void RecordPurgeTime(string cachePath)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(cachePath, LastPurgeFileName),
                DateTime.UtcNow.Ticks.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write last purge time");
        }
    }

    private void TriggerGuideRefreshTask()
    {
        try
        {
            var refreshTask = _taskManager.ScheduledTasks
                .FirstOrDefault(t => string.Equals(t.ScheduledTask.Key, "RefreshGuide", StringComparison.OrdinalIgnoreCase));

            if (refreshTask != null)
            {
                _taskManager.Execute(refreshTask, new TaskOptions());
                _logger.LogInformation("Triggered Jellyfin 'Refresh Guide Data' task after cache purge");
            }
            else
            {
                _logger.LogWarning("Could not find 'RefreshGuide' task. Run 'Refresh Guide Data' manually from Dashboard → Live TV.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to trigger guide refresh task");
        }
    }

    /// <summary>
    /// Result of a guide cache purge operation.
    /// </summary>
    public class PurgeResult
    {
        /// <summary>Gets or sets whether the purge succeeded.</summary>
        public bool Success { get; set; }

        /// <summary>Gets or sets the result message.</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>Gets or sets how many xmltv files were deleted.</summary>
        public int FilesDeleted { get; set; }

        /// <summary>Gets or sets how many *_channels directories were removed.</summary>
        public int DirsDeleted { get; set; }
    }
}
