using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SportsDVR.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SportsDVR.Services;

/// <summary>
/// Manages sports subscriptions (CRUD operations).
/// </summary>
public class SubscriptionManager
{
    private readonly ILogger<SubscriptionManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubscriptionManager"/> class.
    /// </summary>
    public SubscriptionManager(ILogger<SubscriptionManager> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets all subscriptions.
    /// </summary>
    public IReadOnlyList<Subscription> GetAll()
    {
        return Plugin.Instance?.Configuration.Subscriptions ?? new List<Subscription>();
    }

    /// <summary>
    /// Gets all enabled subscriptions.
    /// </summary>
    public IReadOnlyList<Subscription> GetEnabled()
    {
        return GetAll().Where(s => s.Enabled).ToList();
    }

    /// <summary>
    /// Gets a subscription by ID.
    /// </summary>
    public Subscription? GetById(string id)
    {
        return GetAll().FirstOrDefault(s => s.Id == id);
    }

    /// <summary>
    /// Gets subscriptions for a specific team name.
    /// </summary>
    public IReadOnlyList<Subscription> GetByTeam(string teamName)
    {
        return GetAll()
            .Where(s => s.Type == SubscriptionType.Team &&
                        s.Name.Equals(teamName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Adds a new subscription.
    /// </summary>
    public Subscription Add(Subscription subscription)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            throw new InvalidOperationException("Plugin not initialized");
        }

        subscription.Id = Guid.NewGuid().ToString();
        subscription.CreatedAt = DateTime.UtcNow;

        config.Subscriptions.Add(subscription);
        Plugin.Instance!.SaveConfiguration();

        _logger.LogInformation(
            "Added subscription: {Name} ({Type})",
            subscription.Name,
            subscription.Type);

        return subscription;
    }

    /// <summary>
    /// Updates an existing subscription.
    /// </summary>
    public Subscription? Update(Subscription subscription)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return null;
        }

        var existing = config.Subscriptions.FirstOrDefault(s => s.Id == subscription.Id);
        if (existing == null)
        {
            return null;
        }

        // Update fields
        existing.Name = subscription.Name;
        existing.Type = subscription.Type;
        existing.MatchPattern = subscription.MatchPattern;
        existing.ExcludePatterns = subscription.ExcludePatterns;
        existing.Priority = subscription.Priority;
        existing.IncludeReplays = subscription.IncludeReplays;
        existing.Enabled = subscription.Enabled;

        Plugin.Instance!.SaveConfiguration();

        _logger.LogInformation("Updated subscription: {Name}", subscription.Name);

        return existing;
    }

    /// <summary>
    /// Deletes a subscription by ID.
    /// </summary>
    public bool Delete(string id)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return false;
        }

        var subscription = config.Subscriptions.FirstOrDefault(s => s.Id == id);
        if (subscription == null)
        {
            return false;
        }

        config.Subscriptions.Remove(subscription);
        Plugin.Instance!.SaveConfiguration();

        _logger.LogInformation("Deleted subscription: {Name}", subscription.Name);

        return true;
    }

    /// <summary>
    /// Enables or disables a subscription.
    /// </summary>
    public bool SetEnabled(string id, bool enabled)
    {
        var subscription = GetById(id);
        if (subscription == null)
        {
            return false;
        }

        subscription.Enabled = enabled;
        Plugin.Instance!.SaveConfiguration();

        _logger.LogInformation(
            "{Action} subscription: {Name}",
            enabled ? "Enabled" : "Disabled",
            subscription.Name);

        return true;
    }

    /// <summary>
    /// Checks if a team is already subscribed.
    /// </summary>
    public bool IsSubscribed(string name, SubscriptionType type)
    {
        return GetAll().Any(s =>
            s.Type == type &&
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
