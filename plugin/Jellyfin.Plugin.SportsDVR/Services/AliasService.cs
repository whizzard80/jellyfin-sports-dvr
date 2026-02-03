using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsDVR.Services;

/// <summary>
/// Service for resolving team name aliases.
/// Wraps the static TeamAliases class for dependency injection.
/// </summary>
public class AliasService
{
    private readonly ILogger<AliasService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AliasService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public AliasService(ILogger<AliasService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Resolves a team name to its canonical form using aliases.
    /// </summary>
    /// <param name="teamName">The team name to resolve.</param>
    /// <returns>The canonical team name.</returns>
    public string ResolveAlias(string teamName)
    {
        if (string.IsNullOrWhiteSpace(teamName))
        {
            return teamName;
        }

        return TeamAliases.GetCanonicalName(teamName);
    }

    /// <summary>
    /// Gets all known aliases for a team name.
    /// </summary>
    /// <param name="teamName">The team name.</param>
    /// <returns>All known aliases including the canonical name.</returns>
    public IEnumerable<string> GetAllAliases(string teamName)
    {
        return TeamAliases.GetAliases(teamName);
    }

    /// <summary>
    /// Checks if two team names refer to the same team.
    /// </summary>
    /// <param name="name1">First team name.</param>
    /// <param name="name2">Second team name.</param>
    /// <returns>True if the names are equivalent.</returns>
    public bool AreEquivalent(string name1, string name2)
    {
        return TeamAliases.AreEquivalent(name1, name2);
    }

    /// <summary>
    /// Normalizes a team name by removing common suffixes.
    /// </summary>
    /// <param name="teamName">The team name to normalize.</param>
    /// <returns>The normalized team name.</returns>
    public string NormalizeName(string teamName)
    {
        return TeamAliases.NormalizeName(teamName);
    }

    /// <summary>
    /// Builds a regex pattern that matches any variation of a team name.
    /// </summary>
    /// <param name="teamName">The team name.</param>
    /// <returns>A regex pattern string.</returns>
    public string BuildMatchPattern(string teamName)
    {
        return TeamAliases.BuildMatchPattern(teamName);
    }

    /// <summary>
    /// Looks up a team name and returns all known information.
    /// </summary>
    /// <param name="teamName">The team name to look up.</param>
    /// <returns>Lookup result with canonical name and all aliases.</returns>
    public AliasLookupResult Lookup(string teamName)
    {
        var canonical = TeamAliases.GetCanonicalName(teamName);
        var aliases = TeamAliases.GetAliases(teamName).ToList();
        
        return new AliasLookupResult
        {
            InputName = teamName,
            CanonicalName = canonical,
            Aliases = aliases,
            IsKnownTeam = aliases.Count > 1 || !canonical.Equals(teamName, System.StringComparison.OrdinalIgnoreCase)
        };
    }
}

/// <summary>
/// Result of an alias lookup operation.
/// </summary>
public class AliasLookupResult
{
    /// <summary>
    /// Gets or sets the input team name.
    /// </summary>
    public string InputName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the canonical team name.
    /// </summary>
    public string CanonicalName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets all known aliases.
    /// </summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>
    /// Gets or sets whether this is a known team with aliases.
    /// </summary>
    public bool IsKnownTeam { get; set; }
}
