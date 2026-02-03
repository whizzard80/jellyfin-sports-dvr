using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Jellyfin.Plugin.SportsDVR.Models;
using Jellyfin.Plugin.SportsDVR.Services;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="SportsController"/> class.
    /// </summary>
    public SportsController(
        ILogger<SportsController> logger,
        SubscriptionManager subscriptionManager,
        EpgParser epgParser,
        PatternMatcher patternMatcher)
    {
        _logger = logger;
        _subscriptionManager = subscriptionManager;
        _epgParser = epgParser;
        _patternMatcher = patternMatcher;
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

        var subscription = new Subscription
        {
            Name = request.Name,
            Type = request.Type,
            MatchPattern = request.MatchPattern ?? request.Name.ToLowerInvariant(),
            ExcludePatterns = request.ExcludePatterns ?? Array.Empty<string>(),
            Priority = request.Priority ?? Plugin.Instance?.Configuration.DefaultPriority ?? 50,
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
            ScanIntervalMinutes = config?.ScanIntervalMinutes ?? 5
        });
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
    /// Gets or sets the priority.
    /// </summary>
    public int? Priority { get; set; }

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
    /// Gets or sets the EPG scan interval in minutes.
    /// </summary>
    public int ScanIntervalMinutes { get; set; }
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
