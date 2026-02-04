using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SportsDVR.Configuration;
using Jellyfin.Plugin.SportsDVR.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsDVR.Services;

/// <summary>
/// Service for managing sports recordings library.
/// </summary>
public class SportsLibraryService
{
    private readonly ILogger<SportsLibraryService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly SubscriptionManager _subscriptionManager;
    private readonly PatternMatcher _patternMatcher;
    private readonly SportsScorer _sportsScorer;
    private readonly ISessionManager _sessionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SportsLibraryService"/> class.
    /// </summary>
    public SportsLibraryService(
        ILogger<SportsLibraryService> logger,
        ILibraryManager libraryManager,
        SubscriptionManager subscriptionManager,
        PatternMatcher patternMatcher,
        SportsScorer sportsScorer,
        ISessionManager sessionManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _subscriptionManager = subscriptionManager;
        _patternMatcher = patternMatcher;
        _sportsScorer = sportsScorer;
        _sessionManager = sessionManager;
    }

    /// <summary>
    /// Gets recordings grouped by date buckets.
    /// </summary>
    public Task<Dictionary<string, List<SportsRecordingDto>>> GetRecordingsByDateAsync()
    {
        var items = QueryJellyfinLibrary();
        var recordings = ConvertToRecordings(items);

        var result = new Dictionary<string, List<SportsRecordingDto>>
        {
            { "Today", new List<SportsRecordingDto>() },
            { "ThisWeek", new List<SportsRecordingDto>() },
            { "ThisMonth", new List<SportsRecordingDto>() },
            { "Older", new List<SportsRecordingDto>() }
        };

        var now = DateTime.UtcNow;
        var today = now.Date;
        var weekStart = today.AddDays(-(int)today.DayOfWeek);
        var monthStart = new DateTime(today.Year, today.Month, 1);

        foreach (var recording in recordings)
        {
            var recordedDate = recording.RecordedDate.Date;

            if (recordedDate == today)
            {
                result["Today"].Add(recording);
            }
            else if (recordedDate >= weekStart)
            {
                result["ThisWeek"].Add(recording);
            }
            else if (recordedDate >= monthStart)
            {
                result["ThisMonth"].Add(recording);
            }
            else
            {
                result["Older"].Add(recording);
            }
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// Gets recordings for a specific subscription.
    /// </summary>
    public Task<List<SportsRecordingDto>> GetRecordingsForSubscriptionAsync(string subscriptionId)
    {
        var items = QueryJellyfinLibrary();
        var recordings = ConvertToRecordings(items);

        return Task.FromResult(recordings
            .Where(r => r.SubscriptionId == subscriptionId)
            .ToList());
    }

    /// <summary>
    /// Gets an item by ID.
    /// </summary>
    public BaseItem? GetItemById(Guid itemId)
    {
        try
        {
            return _libraryManager.GetItemById(itemId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting item by ID: {Id}", itemId);
            return null;
        }
    }

    /// <summary>
    /// Checks if a file is safe to move.
    /// </summary>
    public bool IsFileSafeToMove(BaseItem item)
    {
        return !ShouldSkipFile(item);
    }

    /// <summary>
    /// Gets the file age for an item.
    /// </summary>
    public TimeSpan GetFileAge(BaseItem item)
    {
        var createdDate = GetItemCreatedDate(item);
        return DateTime.UtcNow - createdDate;
    }

    /// <summary>
    /// Organizes existing recordings by moving them from the DVR folder to the sports folder.
    /// Scans DvrRecordingsPath (source) and moves sports games to SportsRecordingsPath (destination).
    /// </summary>
    public Task<OrganizeRecordingsResponse> OrganizeExistingRecordingsAsync()
    {
        var result = new OrganizeRecordingsResponse();
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            result.Errors.Add("Plugin configuration not found");
            return Task.FromResult(result);
        }

        var sourcePath = config.DvrRecordingsPath;
        var destBasePath = config.SportsRecordingsPath;

        if (string.IsNullOrEmpty(sourcePath))
        {
            result.Errors.Add("DVR Recordings Path (source) not configured");
            return Task.FromResult(result);
        }

        if (string.IsNullOrEmpty(destBasePath))
        {
            result.Errors.Add("Sports DVR Path (destination) not configured");
            return Task.FromResult(result);
        }

        if (!Directory.Exists(sourcePath))
        {
            result.Errors.Add($"DVR Recordings Path does not exist: {sourcePath}");
            return Task.FromResult(result);
        }

        _logger.LogInformation("Organizing recordings from {Source} to {Dest}", sourcePath, destBasePath);

        try
        {
            // Ensure destination exists
            Directory.CreateDirectory(destBasePath);

            // Scan files directly from the DVR folder (not Jellyfin library - may not be indexed yet)
            var videoExtensions = new[] { ".ts", ".mp4", ".mkv", ".avi", ".m4v", ".mov", ".mpg", ".mpeg" };
            var files = Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories)
                .Where(f => videoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Where(f => !f.StartsWith(destBasePath, StringComparison.OrdinalIgnoreCase)) // Skip files already in sports folder
                .ToList();

            result.Scanned = files.Count;
            _logger.LogInformation("Found {Count} video files to scan", files.Count);

            foreach (var filePath in files)
            {
                try
                {
                    // Skip files already in sports folder
                    if (filePath.StartsWith(destBasePath, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Skipped++;
                        continue;
                    }

                    // Safety check: Skip if file is too new (< 1.5 hours old)
                    var fileInfo = new FileInfo(filePath);
                    var fileAge = DateTime.UtcNow - fileInfo.LastWriteTimeUtc;
                    if (fileAge.TotalMinutes < 90)
                    {
                        result.Skipped++;
                        _logger.LogDebug("Skipping file too new (age: {Age} min): {Name}", fileAge.TotalMinutes, fileInfo.Name);
                        continue;
                    }

                    // Check if file is in use
                    if (IsFileInUse(filePath))
                    {
                        result.Skipped++;
                        _logger.LogDebug("Skipping file in use: {Name}", fileInfo.Name);
                        continue;
                    }

                    // Try to find the item in Jellyfin library for better metadata
                    var baseItem = FindJellyfinItemByPath(filePath);
                    
                    // Get title from filename or Jellyfin item
                    var title = baseItem?.Name ?? Path.GetFileNameWithoutExtension(filePath);
                    var overview = baseItem?.Overview ?? "";

                    // Score the program to detect if it's a sports game
                    var scored = _sportsScorer.Score(title, "", overview);
                    
                    // Match to subscription
                    var matchedSub = FindMatchingSubscriptionByTitle(title, scored);

                    // Only move if it matches a subscription OR scores as likely/possible sports
                    if (matchedSub == null && scored.Score < SportsScorer.THRESHOLD_POSSIBLE_GAME)
                    {
                        _logger.LogDebug("Not a sports game (score {Score}): {Title}", scored.Score, title);
                        continue;
                    }

                    result.Matched++;
                    _logger.LogInformation("Matched sports game: {Title} (score: {Score}, subscription: {Sub})", 
                        title, scored.Score, matchedSub?.Name ?? "none");

                    // Determine destination folder
                    var destFolder = GetDestinationFolderFromScored(config, scored, matchedSub, DateTime.UtcNow);
                    
                    // Generate clean filename with periods instead of spaces
                    var cleanFilename = GenerateCleanFilename(scored, matchedSub, fileInfo.Extension, DateTime.UtcNow);
                    var destFilePath = Path.Combine(destBasePath, destFolder, cleanFilename);

                    // Ensure destination directory exists
                    Directory.CreateDirectory(Path.GetDirectoryName(destFilePath)!);

                    // Final safety check
                    if (IsFileInUse(filePath))
                    {
                        result.Skipped++;
                        _logger.LogWarning("File became in use, skipping: {Name}", fileInfo.Name);
                        continue;
                    }

                    // Check if Jellyfin is playing this item
                    if (baseItem != null && IsItemCurrentlyPlaying(baseItem.Id))
                    {
                        result.Skipped++;
                        _logger.LogWarning("Item is being played, skipping: {Name}", title);
                        continue;
                    }

                    // Move file
                    File.Move(filePath, destFilePath);
                    result.Moved++;

                    _logger.LogInformation("✅ Moved: {Name} -> {Dest}", fileInfo.Name, destFilePath);

                    // Update Jellyfin metadata if item exists in library
                    if (baseItem != null)
                    {
                        var cleanTitle = GenerateCleanTitleFromScored(scored, matchedSub, DateTime.UtcNow);
                        var cleanDescription = RemoveSpoilers(overview);
                        if (!string.IsNullOrEmpty(cleanTitle) && cleanTitle != baseItem.Name)
                        {
                            UpdateItemMetadata(baseItem, cleanTitle, cleanDescription);
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    var errorMsg = $"Failed to process {Path.GetFileName(filePath)}: {ex.Message}";
                    result.Errors.Add(errorMsg);
                    _logger.LogError(ex, "Error organizing file: {Path}", filePath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during organization");
            result.Errors.Add($"Organization failed: {ex.Message}");
        }

        _logger.LogInformation("Organization complete: Scanned={Scanned}, Matched={Matched}, Moved={Moved}, Skipped={Skipped}, Failed={Failed}",
            result.Scanned, result.Matched, result.Moved, result.Skipped, result.Failed);

        return Task.FromResult(result);
    }

    /// <summary>
    /// Finds a Jellyfin library item by its file path.
    /// </summary>
    private BaseItem? FindJellyfinItemByPath(string filePath)
    {
        try
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                IsVirtualItem = false
            };

            var items = _libraryManager.GetItemsResult(query);
            return items.Items.FirstOrDefault(i => 
                !string.IsNullOrEmpty(i.Path) && 
                i.Path.Equals(filePath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error finding Jellyfin item by path: {Path}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Finds a matching subscription by title.
    /// </summary>
    private Subscription? FindMatchingSubscriptionByTitle(string title, ScoredProgram scored)
    {
        var subscriptions = _subscriptionManager.GetAll().Where(s => s.Enabled).ToList();
        
        foreach (var subscription in subscriptions)
        {
            var parsed = new ParsedProgram
            {
                OriginalTitle = title,
                Team1 = scored.Team1,
                Team2 = scored.Team2,
                League = scored.DetectedLeague,
                IsReplay = scored.IsReplay
            };

            if (_patternMatcher.Matches(parsed, subscription))
            {
                return subscription;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets destination folder from scored program data.
    /// </summary>
    private string GetDestinationFolderFromScored(PluginConfiguration config, ScoredProgram scored, Subscription? subscription, DateTime date)
    {
        switch (config.FolderOrganization.ToLowerInvariant())
        {
            case "subscription":
                if (subscription != null)
                {
                    return SanitizeFolderName(subscription.Name);
                }
                if (!string.IsNullOrEmpty(scored.DetectedLeague))
                {
                    return SanitizeFolderName(scored.DetectedLeague);
                }
                if (!string.IsNullOrEmpty(scored.Team1))
                {
                    return SanitizeFolderName(scored.Team1);
                }
                return "Other";
                
            case "date":
                return date.ToString("yyyy-MM-dd");
                
            case "league":
                var league = scored.DetectedLeague ?? subscription?.Name ?? "Other";
                return Path.Combine(SanitizeFolderName(league), date.ToString("yyyy-MM-dd"));
                
            case "league_date":
                var league2 = scored.DetectedLeague ?? subscription?.Name ?? "Other";
                return $"{SanitizeFolderName(league2)}_{date:yyyy-MM-dd}";
                
            default:
                return "";
        }
    }

    /// <summary>
    /// Generates a clean title from scored data.
    /// </summary>
    private string GenerateCleanTitleFromScored(ScoredProgram scored, Subscription? subscription, DateTime date)
    {
        var team1 = scored.Team1 ?? "";
        var team2 = scored.Team2 ?? "";
        var dateStr = date.ToString("MMM d, yyyy");
        
        // Remove city prefixes for cleaner display
        team1 = RemoveCityPrefix(team1);
        team2 = RemoveCityPrefix(team2);
        
        if (!string.IsNullOrEmpty(team1) && !string.IsNullOrEmpty(team2))
        {
            return $"{team1} vs {team2} - {dateStr}";
        }
        
        // Fallback: Use subscription name or league
        if (subscription != null)
        {
            return $"{subscription.Name} - {dateStr}";
        }
        
        if (!string.IsNullOrEmpty(scored.DetectedLeague))
        {
            return $"{scored.DetectedLeague} - {dateStr}";
        }
        
        return $"Sports Recording - {dateStr}";
    }

    /// <summary>
    /// Generates a clean filename with periods instead of spaces.
    /// The Jellyfin metadata (display title) will still use normal formatting.
    /// </summary>
    private string GenerateCleanFilename(ScoredProgram scored, Subscription? subscription, string extension, DateTime date)
    {
        var team1 = scored.Team1 ?? "";
        var team2 = scored.Team2 ?? "";
        var dateStr = date.ToString("yyyy-MM-dd");
        
        // Remove city prefixes for cleaner filenames
        team1 = RemoveCityPrefix(team1);
        team2 = RemoveCityPrefix(team2);
        
        string baseName;
        if (!string.IsNullOrEmpty(team1) && !string.IsNullOrEmpty(team2))
        {
            baseName = $"{team1}.vs.{team2}.{dateStr}";
        }
        else if (subscription != null)
        {
            baseName = $"{subscription.Name}.{dateStr}";
        }
        else if (!string.IsNullOrEmpty(scored.DetectedLeague))
        {
            baseName = $"{scored.DetectedLeague}.{dateStr}";
        }
        else
        {
            baseName = $"Sports.Recording.{dateStr}";
        }
        
        // Replace spaces with periods and sanitize
        baseName = baseName.Replace(" ", ".");
        baseName = SanitizeFilename(baseName);
        
        // Ensure extension starts with a dot
        if (!extension.StartsWith('.'))
        {
            extension = "." + extension;
        }
        
        return baseName + extension;
    }

    /// <summary>
    /// Sanitizes a string for use as a filename.
    /// </summary>
    private static string SanitizeFilename(string filename)
    {
        // Remove invalid filename characters
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in invalid)
        {
            filename = filename.Replace(c, '_');
        }
        
        // Replace multiple periods with single period
        while (filename.Contains(".."))
        {
            filename = filename.Replace("..", ".");
        }
        
        return filename;
    }

    /// <summary>
    /// Queries Jellyfin library for recordings.
    /// </summary>
    private List<BaseItem> QueryJellyfinLibrary()
    {
        var config = Plugin.Instance?.Configuration;
        var libraryName = config?.SportsLibraryName ?? "Sports DVR";

        try
        {
            // Try to find library by name
            var libraries = _libraryManager.RootFolder.Children
                .OfType<CollectionFolder>()
                .Where(f => f.Name.Equals(libraryName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (libraries.Count > 0)
            {
                var library = libraries[0];
                var query = new InternalItemsQuery
                {
                    AncestorIds = new[] { library.Id },
                    Recursive = true,
                    IsVirtualItem = false
                };

                var items = _libraryManager.GetItemsResult(query);
                return items.Items
                    .Where(item => item is Video)
                    .ToList();
            }

            // Fallback: Query all libraries and filter by path
            var recordingsPath = config?.SportsRecordingsPath ?? "/mnt/movies/hyperdata/dvr/sports-dvr/";
            var allQuery = new InternalItemsQuery
            {
                Recursive = true,
                IsVirtualItem = false
            };

            var allItems = _libraryManager.GetItemsResult(allQuery);
            return allItems.Items
                .Where(item => item is Video && !string.IsNullOrEmpty(item.Path) &&
                    item.Path.StartsWith(recordingsPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying Jellyfin library");
            return new List<BaseItem>();
        }
    }

    /// <summary>
    /// Converts BaseItem list to SportsRecordingDto list.
    /// </summary>
    private List<SportsRecordingDto> ConvertToRecordings(List<BaseItem> items)
    {
        var recordings = new List<SportsRecordingDto>();

        foreach (var item in items)
        {
            try
            {
                var title = item.Name ?? "";
                var overview = item.Overview ?? "";
                var channelName = "";

                // Score the program
                var scored = _sportsScorer.Score(title, channelName, overview);
                
                // Match to subscription
                var matchedSub = FindMatchingSubscription(item, scored);

                // Get recorded date
                var recordedDate = GetItemRecordedDate(item);

                var recording = new SportsRecordingDto
                {
                    Id = item.Id.ToString(),
                    Name = GenerateCleanTitle(item, scored, matchedSub),
                    OriginalTitle = title,
                    FilePath = item.Path,
                    Team1 = scored.Team1,
                    Team2 = scored.Team2,
                    League = scored.DetectedLeague,
                    SubscriptionName = matchedSub?.Name,
                    SubscriptionId = matchedSub?.Id,
                    RecordedDate = recordedDate,
                    RunTimeTicks = item.RunTimeTicks,
                    FileSize = item.Size,
                    Overview = RemoveSpoilers(overview),
                    ChannelName = channelName,
                    PlaybackUrl = $"/web/index.html#!/itemdetails.html?id={item.Id}"
                };

                // Set image URL if available
                if (item.HasImage(ImageType.Primary))
                {
                    recording.ImageUrl = $"/Items/{item.Id}/Images/Primary";
                }

                recordings.Add(recording);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error converting item to recording: {Id}", item.Id);
            }
        }

        return recordings.OrderByDescending(r => r.RecordedDate).ToList();
    }

    /// <summary>
    /// Gets the recorded date for an item, preferring PremiereDate, then DateCreated.
    /// </summary>
    private DateTime GetItemRecordedDate(BaseItem item)
    {
        if (item.PremiereDate.HasValue && item.PremiereDate.Value != DateTime.MinValue)
        {
            return item.PremiereDate.Value;
        }
        
        return GetItemCreatedDate(item);
    }

    /// <summary>
    /// Gets the created date for an item.
    /// </summary>
    private DateTime GetItemCreatedDate(BaseItem item)
    {
        // DateCreated is a non-nullable DateTime, so check if it's not the default value
        if (item.DateCreated != default(DateTime) && item.DateCreated != DateTime.MinValue)
        {
            return item.DateCreated;
        }
        
        return DateTime.UtcNow;
    }

    /// <summary>
    /// Finds a matching subscription for an item.
    /// </summary>
    private Subscription? FindMatchingSubscription(BaseItem item, ScoredProgram scored)
    {
        var subscriptions = _subscriptionManager.GetAll().Where(s => s.Enabled).ToList();
        var title = item.Name ?? "";
        
        foreach (var subscription in subscriptions)
        {
            var parsed = new ParsedProgram
            {
                OriginalTitle = title,
                Team1 = scored.Team1,
                Team2 = scored.Team2,
                League = scored.DetectedLeague,
                IsReplay = scored.IsReplay
            };

            if (_patternMatcher.Matches(parsed, subscription))
            {
                return subscription;
            }
        }

        return null;
    }

    /// <summary>
    /// Generates a clean, spoiler-free title.
    /// </summary>
    private string GenerateCleanTitle(BaseItem item, ScoredProgram scored, Subscription? subscription)
    {
        var team1 = scored.Team1 ?? "";
        var team2 = scored.Team2 ?? "";
        var date = GetItemRecordedDate(item);
        var dateStr = date.ToString("MMM d, yyyy");
        
        // Remove city prefixes for cleaner display
        team1 = RemoveCityPrefix(team1);
        team2 = RemoveCityPrefix(team2);
        
        if (!string.IsNullOrEmpty(team1) && !string.IsNullOrEmpty(team2))
        {
            return $"{team1} vs {team2} - {dateStr}";
        }
        
        // Fallback: Use subscription name or league
        if (subscription != null)
        {
            return $"{subscription.Name} - {dateStr}";
        }
        
        if (!string.IsNullOrEmpty(scored.DetectedLeague))
        {
            return $"{scored.DetectedLeague} - {dateStr}";
        }
        
        // Last resort: Clean up original title
        return CleanTitle(item.Name ?? "", dateStr);
    }

    /// <summary>
    /// Removes city prefixes from team names.
    /// </summary>
    private string RemoveCityPrefix(string teamName)
    {
        if (string.IsNullOrEmpty(teamName))
        {
            return "";
        }

        // Common city prefixes to remove
        var prefixes = new[] { "Boston ", "Los Angeles ", "New York ", "San Francisco ", "Dallas ", "Houston ", "Chicago ", "Miami ", "Philadelphia ", "Phoenix ", "Seattle ", "Denver ", "Portland ", "Atlanta ", "Detroit ", "Cleveland ", "Milwaukee ", "Minnesota ", "Oklahoma City ", "Utah ", "Sacramento ", "Orlando ", "Charlotte ", "Washington ", "Brooklyn ", "New Orleans ", "Memphis ", "Indiana ", "Toronto ", "Manchester ", "London ", "Liverpool ", "Arsenal ", "Chelsea ", "Tottenham ", "Leicester ", "Brighton ", "Wolverhampton ", "West Ham ", "Crystal Palace ", "Everton ", "Newcastle ", "Southampton ", "Aston Villa ", "Bournemouth ", "Fulham ", "Brentford ", "Nottingham ", "Sheffield ", "Burnley ", "Luton " };

        foreach (var prefix in prefixes)
        {
            if (teamName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return teamName.Substring(prefix.Length);
            }
        }

        return teamName;
    }

    /// <summary>
    /// Cleans up a title by removing spoilers.
    /// </summary>
    private string CleanTitle(string title, string dateStr)
    {
        // Remove common spoiler patterns
        var cleaned = Regex.Replace(title, @"\d+\s*[-–]\s*\d+", "", RegexOptions.IgnoreCase); // Scores
        cleaned = Regex.Replace(cleaned, @"\b(final|complete|finished)\b", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        
        if (string.IsNullOrEmpty(cleaned))
        {
            return $"Sports Recording - {dateStr}";
        }

        return $"{cleaned} - {dateStr}";
    }

    /// <summary>
    /// Removes spoilers from description.
    /// </summary>
    private string RemoveSpoilers(string? description)
    {
        if (string.IsNullOrEmpty(description))
        {
            return "";
        }
        
        var spoilerPatterns = new[]
        {
            @"\d+\s*[-–]\s*\d+",  // Scores like "108-95"
            @"\d+\s*to\s*\d+",     // "108 to 95"
            @"final\s*score",      // "Final Score"
            @"won\s+by",           // "won by"
            @"defeated",           // "defeated"
            @"beat",               // "beat"
            @"lost\s+to"           // "lost to"
        };
        
        var cleaned = description;
        foreach (var pattern in spoilerPatterns)
        {
            cleaned = Regex.Replace(cleaned, pattern, "", RegexOptions.IgnoreCase);
        }
        
        return cleaned.Trim();
    }

    /// <summary>
    /// Determines destination folder based on organization strategy.
    /// </summary>
    private string GetDestinationFolder(PluginConfiguration config, BaseItem item, ScoredProgram scored, Subscription? subscription)
    {
        var date = GetItemRecordedDate(item);
        
        switch (config.FolderOrganization.ToLowerInvariant())
        {
            case "subscription":
                if (subscription != null)
                {
                    return SanitizeFolderName(subscription.Name);
                }
                if (!string.IsNullOrEmpty(scored.DetectedLeague))
                {
                    return SanitizeFolderName(scored.DetectedLeague);
                }
                if (!string.IsNullOrEmpty(scored.Team1))
                {
                    return SanitizeFolderName(scored.Team1);
                }
                return "Other";
                
            case "date":
                return date.ToString("yyyy-MM-dd");
                
            case "league":
                var league = scored.DetectedLeague ?? subscription?.Name ?? "Other";
                return Path.Combine(SanitizeFolderName(league), date.ToString("yyyy-MM-dd"));
                
            case "league_date":
                var league2 = scored.DetectedLeague ?? subscription?.Name ?? "Other";
                return $"{SanitizeFolderName(league2)}_{date:yyyy-MM-dd}";
                
            default:
                return "";
        }
    }

    /// <summary>
    /// Sanitizes folder name to be filesystem-safe.
    /// </summary>
    private string SanitizeFolderName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Unknown";
        }
        
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();
        
        if (sanitized.Length > 100)
        {
            sanitized = sanitized.Substring(0, 100);
        }
        
        return sanitized;
    }

    /// <summary>
    /// Checks if a file is currently in use.
    /// </summary>
    private bool IsFileInUse(string filePath)
    {
        try
        {
            using (var stream = File.Open(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                return false;
            }
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Checks if an item is currently being played.
    /// </summary>
    private bool IsItemCurrentlyPlaying(Guid itemId)
    {
        try
        {
            var sessions = _sessionManager.Sessions;
            foreach (var session in sessions)
            {
                if (session.NowPlayingItem?.Id == itemId)
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking playback status for item {Id}", itemId);
            return true; // Err on the side of caution
        }
    }

    /// <summary>
    /// Determines if a file should be skipped during organization.
    /// </summary>
    private bool ShouldSkipFile(BaseItem item)
    {
        // Get date from BaseItem
        var itemDate = GetItemCreatedDate(item);
        var fileAge = DateTime.UtcNow - itemDate;
        if (fileAge.TotalMinutes < 90) // Skip files less than 1.5 hours old
        {
            return true;
        }
        
        if (IsItemCurrentlyPlaying(item.Id))
        {
            return true;
        }
        
        // Check if file is in use
        var filePath = item.Path;
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            if (IsFileInUse(filePath))
            {
                return true;
            }
        }
        
        return false;
    }

    /// <summary>
    /// Updates Jellyfin item metadata.
    /// </summary>
    private void UpdateItemMetadata(BaseItem item, string cleanTitle, string? cleanDescription)
    {
        try
        {
            item.Name = cleanTitle;
            if (!string.IsNullOrEmpty(cleanDescription))
            {
                item.Overview = cleanDescription;
            }
            
            // In Jellyfin 10.10+, use UpdateToRepositoryAsync or similar method
            // Different Jellyfin versions may have different update methods
            try
            {
                // Try common update methods
                item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception updateEx)
            {
                // Log but don't fail - metadata update is optional
                _logger.LogDebug(updateEx, "Could not update metadata via UpdateToRepositoryAsync for item {Id}", item.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update metadata for item {Id}", item.Id);
        }
    }
}
