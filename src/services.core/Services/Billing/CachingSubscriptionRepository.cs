// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// A caching decorator over <see cref="ISubscriptionRepository"/> that caches a tenant's
/// subscription in Redis with a short TTL. Subscription status is read on every state-changing
/// request (see SubscriptionStatusPreProcessor) and on every unary telemetry call, so this removes
/// a database round-trip from those hot paths. The cache is correct across replicas because it is
/// backed by Redis, and is invalidated immediately on any subscription-mutation method routed
/// through this repository. The TTL bounds staleness for any path that cannot be mapped to a
/// concrete tenant key.
/// </summary>
public sealed class CachingSubscriptionRepository : ISubscriptionRepository
{
    private const string KeyPrefix = "subscription:tenant:";

    // Sentinel cached when a tenant has no subscription, so the negative result is cached without
    // relying on empty-string semantics of RedisValue (an empty value is ambiguous with absence).
    private const string NegativeMarker = "__none__";

    private readonly ISubscriptionRepository _inner;
    private readonly IConnectionMultiplexer _redis;
    private readonly TimeSpan _ttl;

    /// <summary>
    /// Initializes a new instance of the <see cref="CachingSubscriptionRepository"/> class.
    /// </summary>
    /// <param name="inner">The underlying repository that performs the actual database work.</param>
    /// <param name="redis">The Redis connection multiplexer used as the cache backing store.</param>
    /// <param name="redisOptions">Redis options supplying the cache TTL.</param>
    public CachingSubscriptionRepository(
        ISubscriptionRepository inner,
        IConnectionMultiplexer redis,
        IOptions<RedisOptions> redisOptions)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(redisOptions);

        _inner = inner;
        _redis = redis;

        int ttlSeconds = redisOptions.Value.SubscriptionCacheTtlSeconds;
        _ttl = TimeSpan.FromSeconds(ttlSeconds > 0 ? ttlSeconds : 30);
    }

    /// <inheritdoc/>
    public async Task<TenantSubscription?> GetSubscriptionForTenantAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        IDatabase db = _redis.GetDatabase();
        string key = KeyFor(tenantId);

        RedisValue cached = await db.StringGetAsync(key);
        if (cached.HasValue)
        {
            // A present-but-empty value is a cached negative (tenant has no subscription).
            // Either way the key existing means we have an authoritative cached answer.
            return Deserialize(cached!);
        }

        TenantSubscription? subscription = await _inner.GetSubscriptionForTenantAsync(tenantId, cancellationToken);

        // Cache both hits and misses so a tenant with no subscription does not re-query every call.
        string payload = subscription is null
            ? NegativeMarker
            : JsonSerializer.Serialize(subscription, JsonDefaults.CamelCase);
        await db.StringSetAsync(key, payload, _ttl, false, When.Always, CommandFlags.None);

        return subscription;
    }

    /// <inheritdoc/>
    public async Task<TenantSubscription> CreateTenantSubscriptionAsync(TenantSubscription subscription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        TenantSubscription result = await _inner.CreateTenantSubscriptionAsync(subscription, cancellationToken);
        await InvalidateAsync(subscription.TenantId);

        return result;
    }

    /// <inheritdoc/>
    public async Task UpdateSubscriptionOnCheckoutAsync(int tenantId, SubscriptionTier tier, CancellationToken cancellationToken = default)
    {
        await _inner.UpdateSubscriptionOnCheckoutAsync(tenantId, tier, cancellationToken);
        await InvalidateAsync(tenantId);
    }

    /// <inheritdoc/>
    public async Task UpdateSubscriptionPeriodEndAsync(int tenantId, DateTimeOffset currentPeriodEnd, CancellationToken cancellationToken = default)
    {
        await _inner.UpdateSubscriptionPeriodEndAsync(tenantId, currentPeriodEnd, cancellationToken);
        await InvalidateAsync(tenantId);
    }

    /// <inheritdoc/>
    public async Task RevertSubscriptionToFreeAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        await _inner.RevertSubscriptionToFreeAsync(tenantId, cancellationToken);
        await InvalidateAsync(tenantId);
    }

    /// <inheritdoc/>
    public async Task SetSubscriptionPastDueAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        await _inner.SetSubscriptionPastDueAsync(tenantId, cancellationToken);
        await InvalidateAsync(tenantId);
    }

    /// <inheritdoc/>
    public async Task SetSubscriptionActiveAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        await _inner.SetSubscriptionActiveAsync(tenantId, cancellationToken);
        await InvalidateAsync(tenantId);
    }

    /// <inheritdoc/>
    public async Task DowngradeSubscriptionToProAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        await _inner.DowngradeSubscriptionToProAsync(tenantId, cancellationToken);
        await InvalidateAsync(tenantId);
    }

    /// <inheritdoc/>
    public async Task DeactivateSubscriptionAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        await _inner.DeactivateSubscriptionAsync(tenantId, cancellationToken);
        await InvalidateAsync(tenantId);
    }

    /// <inheritdoc/>
    public async Task<List<TenantSubscription>> GetPaidSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        return await _inner.GetPaidSubscriptionsAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<TenantSubscription> InsertSubscriptionAsync(TenantSubscription subscription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        TenantSubscription result = await _inner.InsertSubscriptionAsync(subscription, cancellationToken);
        await InvalidateAsync(subscription.TenantId);

        return result;
    }

    /// <inheritdoc/>
    public async Task ReactivateFreeSubscriptionAsync(int subscriptionId, CancellationToken cancellationToken = default)
    {
        // This mutation is keyed by subscription ID, not tenant ID, so the specific tenant cache
        // key cannot be derived cheaply. Staleness here is bounded by the short TTL.
        await _inner.ReactivateFreeSubscriptionAsync(subscriptionId, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> UpdateSubscriptionAdminAsync(int tenantId, SubscriptionTier tier, SubscriptionStatus status, CancellationToken cancellationToken = default)
    {
        int updated = await _inner.UpdateSubscriptionAdminAsync(tenantId, tier, status, cancellationToken);
        await InvalidateAsync(tenantId);

        return updated;
    }

    /// <inheritdoc/>
    public async Task<List<TenantSubscription>> GetSubscriptionsForTenantsAsync(List<int> tenantIds, CancellationToken cancellationToken = default)
    {
        return await _inner.GetSubscriptionsForTenantsAsync(tenantIds, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task SetCancelAtPeriodEndAsync(int tenantId, bool cancelAtPeriodEnd, CancellationToken cancellationToken = default)
    {
        await _inner.SetCancelAtPeriodEndAsync(tenantId, cancelAtPeriodEnd, cancellationToken);
        await InvalidateAsync(tenantId);
    }

    private static string KeyFor(int tenantId)
    {
        return KeyPrefix + tenantId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static TenantSubscription? Deserialize(string payload)
    {
        // The negative marker (or any empty value) is a cached negative — tenant has no subscription.
        if (string.IsNullOrEmpty(payload) || string.Equals(payload, NegativeMarker, StringComparison.Ordinal))
        {
            return null;
        }

        return JsonSerializer.Deserialize<TenantSubscription>(payload, JsonDefaults.CamelCase);
    }

    private async Task InvalidateAsync(int tenantId)
    {
        IDatabase db = _redis.GetDatabase();
        await db.KeyDeleteAsync(KeyFor(tenantId));
    }
}
