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

    private readonly ISubscriptionRepository _inner;
    private readonly IConnectionMultiplexer _redis;
    private readonly TimeSpan _ttl;

    /// <summary>
    /// The cached payload for a tenant: the subscription (null when the tenant has none) together with
    /// the effective retention days resolved at cache-population time. Caching both in one entry lets
    /// the telemetry ingest hot path read a tenant's retention without an extra database round-trip.
    /// </summary>
    private sealed record CachedSubscriptionEntry(TenantSubscription? Subscription, int EffectiveRetentionDays);

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
        CachedSubscriptionEntry entry = await GetOrLoadEntryAsync(tenantId, cancellationToken);

        return entry.Subscription;
    }

    /// <inheritdoc/>
    public async Task<int> GetEffectiveRetentionDaysAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        CachedSubscriptionEntry entry = await GetOrLoadEntryAsync(tenantId, cancellationToken);

        return entry.EffectiveRetentionDays;
    }

    /// <inheritdoc/>
    public Task InvalidateSubscriptionCacheAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        return InvalidateAsync(tenantId);
    }

    /// <summary>
    /// Returns the cached subscription-plus-retention entry for a tenant, loading and caching it on a
    /// miss. Both the subscription and its effective retention are resolved by the inner repository so
    /// the resolution rule lives in one place; the pair is cached under a single key so the second of
    /// two reads in a request is served from the cache.
    /// </summary>
    private async Task<CachedSubscriptionEntry> GetOrLoadEntryAsync(int tenantId, CancellationToken cancellationToken)
    {
        IDatabase db = _redis.GetDatabase();
        string key = KeyFor(tenantId);

        RedisValue cached = await db.StringGetAsync(key);
        if (cached.HasValue)
        {
            return Deserialize(cached!);
        }

        TenantSubscription? subscription = await _inner.GetSubscriptionForTenantAsync(tenantId, cancellationToken);
        int effectiveRetentionDays = await _inner.GetEffectiveRetentionDaysAsync(tenantId, cancellationToken);
        CachedSubscriptionEntry entry = new(subscription, effectiveRetentionDays);

        // Cache both hits and misses so a tenant with no subscription does not re-query every call.
        string payload = JsonSerializer.Serialize(entry, JsonDefaults.CamelCase);
        await db.StringSetAsync(key, payload, _ttl, false, When.Always, CommandFlags.None);

        return entry;
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
    public async Task<int> UpdateSubscriptionStateAsync(
        int tenantId,
        SubscriptionTier? tier,
        SubscriptionStatus status,
        bool clearCurrentPeriodEnd = false,
        CancellationToken cancellationToken = default)
    {
        int updated = await _inner.UpdateSubscriptionStateAsync(tenantId, tier, status, clearCurrentPeriodEnd, cancellationToken);
        await InvalidateAsync(tenantId);

        return updated;
    }

    /// <inheritdoc/>
    public async Task UpdateSubscriptionPeriodEndAsync(int tenantId, DateTimeOffset currentPeriodEnd, CancellationToken cancellationToken = default)
    {
        await _inner.UpdateSubscriptionPeriodEndAsync(tenantId, currentPeriodEnd, cancellationToken);
        await InvalidateAsync(tenantId);
    }

    /// <inheritdoc/>
    public async Task<List<TenantSubscription>> GetPaidSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        return await _inner.GetPaidSubscriptionsAsync(cancellationToken);
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

    private static CachedSubscriptionEntry Deserialize(string payload)
    {
        // An empty value is treated as a cached negative with the fail-safe one-day retention, so a
        // corrupt or legacy entry can never resurrect a subscription or a longer retention.
        if (string.IsNullOrEmpty(payload))
        {
            return new CachedSubscriptionEntry(null, 1);
        }

        return JsonSerializer.Deserialize<CachedSubscriptionEntry>(payload, JsonDefaults.CamelCase)
            ?? new CachedSubscriptionEntry(null, 1);
    }

    private async Task InvalidateAsync(int tenantId)
    {
        IDatabase db = _redis.GetDatabase();
        await db.KeyDeleteAsync(KeyFor(tenantId));
    }
}
