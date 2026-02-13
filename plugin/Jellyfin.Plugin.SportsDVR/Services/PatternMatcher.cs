using System;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SportsDVR.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsDVR.Services;

/// <summary>
/// Matches EPG programs against subscriptions using pattern matching.
/// </summary>
public class PatternMatcher
{
    private readonly ILogger<PatternMatcher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PatternMatcher"/> class.
    /// </summary>
    public PatternMatcher(ILogger<PatternMatcher> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks if a parsed program matches a subscription.
    /// </summary>
    /// <param name="program">The parsed EPG program.</param>
    /// <param name="subscription">The subscription to match against.</param>
    /// <returns>True if the program should be recorded for this subscription.</returns>
    public bool Matches(ParsedProgram program, Subscription subscription)
    {
        if (!subscription.Enabled)
        {
            return false;
        }

        // Check replay filter
        if (program.IsReplay && !subscription.IncludeReplays)
        {
            _logger.LogDebug(
                "Skipping {Title} - replay detected and subscription excludes replays",
                program.OriginalTitle);
            return false;
        }

        // Check exclusion patterns first
        if (MatchesExclusions(program, subscription))
        {
            _logger.LogDebug(
                "Skipping {Title} - matched exclusion pattern",
                program.OriginalTitle);
            return false;
        }

        // Match based on subscription type
        var matches = subscription.Type switch
        {
            SubscriptionType.Team => MatchesTeam(program, subscription),
            SubscriptionType.League => MatchesLeague(program, subscription),
            SubscriptionType.Event => MatchesEvent(program, subscription),
            _ => false
        };

        if (matches)
        {
            _logger.LogDebug(
                "Program {Title} matches subscription {SubName}",
                program.OriginalTitle,
                subscription.Name);
        }

        return matches;
    }

    private bool MatchesTeam(ParsedProgram program, Subscription subscription)
    {
        // For team subscriptions, we require a matchup pattern (vs/@/v)
        // to avoid false positives like documentaries
        if (string.IsNullOrEmpty(program.Team1) && string.IsNullOrEmpty(program.Team2))
        {
            // No teams detected - this might be a documentary or show about the team
            // Only match if it's explicitly in Sports category
            if (!program.IsSports)
            {
                return false;
            }
        }

        // Check if alias matching is enabled
        var config = Plugin.Instance?.Configuration;
        if (config?.EnableAliasMatching == true)
        {
            // Check if either team matches via aliases
            if (!string.IsNullOrEmpty(program.Team1) &&
                TeamAliases.AreEquivalent(program.Team1, subscription.Name))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(program.Team2) &&
                TeamAliases.AreEquivalent(program.Team2, subscription.Name))
            {
                return true;
            }
        }

        // Fall back to pattern matching
        return MatchesPattern(program, subscription.MatchPattern);
    }

    private bool MatchesLeague(ParsedProgram program, Subscription subscription)
    {
        // For league subscriptions, check the detected league first
        if (!string.IsNullOrEmpty(program.League))
        {
            if (program.League.Equals(subscription.Name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Fall back to pattern matching
        return MatchesPattern(program, subscription.MatchPattern);
    }

    private bool MatchesEvent(ParsedProgram program, Subscription subscription)
    {
        // For event series (UFC, WWE, F1), just pattern match
        // These don't typically have vs patterns
        return MatchesPattern(program, subscription.MatchPattern);
    }

    private bool MatchesPattern(ParsedProgram program, string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        var searchText = BuildSearchText(program);

        // Check if pattern is a regex (starts with /)
        if (pattern.StartsWith('/'))
        {
            return MatchesRegex(searchText, pattern);
        }

        // Simple case-insensitive contains
        return searchText.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesRegex(string text, string regexPattern)
    {
        try
        {
            // Parse regex pattern like "/pattern/i"
            var match = Regex.Match(regexPattern, @"^/(.+)/([imsx]*)$");
            if (!match.Success)
            {
                // Invalid regex format, treat as literal
                return text.Contains(regexPattern, StringComparison.OrdinalIgnoreCase);
            }

            var pattern = match.Groups[1].Value;
            var flags = match.Groups[2].Value;

            var options = RegexOptions.None;
            if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
            if (flags.Contains('m')) options |= RegexOptions.Multiline;
            if (flags.Contains('s')) options |= RegexOptions.Singleline;
            if (flags.Contains('x')) options |= RegexOptions.IgnorePatternWhitespace;

            return Regex.IsMatch(text, pattern, options);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invalid regex pattern: {Pattern}", regexPattern);
            return false;
        }
    }

    private bool MatchesExclusions(ParsedProgram program, Subscription subscription)
    {
        if (subscription.ExcludePatterns == null || subscription.ExcludePatterns.Length == 0)
        {
            return false;
        }

        var searchText = BuildSearchText(program);

        foreach (var exclusion in subscription.ExcludePatterns)
        {
            if (string.IsNullOrEmpty(exclusion))
            {
                continue;
            }

            if (exclusion.StartsWith('/'))
            {
                if (MatchesRegex(searchText, exclusion))
                {
                    return true;
                }
            }
            else if (searchText.Contains(exclusion, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildSearchText(ParsedProgram program)
    {
        // Build a comprehensive search string from all program data
        var parts = new[]
        {
            program.OriginalTitle,
            program.CleanTitle,
            program.Team1,
            program.Team2,
            program.League,
            program.Category
        };

        return string.Join(" ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }
}
