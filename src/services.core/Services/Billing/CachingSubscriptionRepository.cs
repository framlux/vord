// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Options;
using Hangfire;
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
    // Versioned key prefix. The cached payload shape is part of the cache contract, so the prefix is
    // bumped whenever that shape changes (v2 added the effective-retention field). A single shared
    // constant keeps every read/write/invalidate site on the same version, and the version bump keeps
    // a rolling deploy from reading a peer replica's older-format entry. Kept as one constant so the
    // sites can never drift.
    private const string KeyPrefix = "subscription:tenant:v2:";

    private readonly ISubscriptionRepository _inner;
    private readonly IConnectionMultiplexer _redis;
    private readonly IBackgroundJobClient _backgroundJobs;
    private readonly ILogger<CachingSubscriptionRepository> _logger;
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
    /// <param name="backgroundJobs">Hangfire client used to enqueue retention reclassification after a tier change.</param>
    /// <param name="logger">The logger.</param>
    public CachingSubscriptionRepository(
        ISubscriptionRepository inner,
        IConnectionMultiplexer redis,
        IOptions<RedisOptions> redisOptions,
        IBackgroundJobClient backgroundJobs,
        ILogger<CachingSubscriptionRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(redisOptions);
        ArgumentNullException.ThrowIfNull(backgroundJobs);
        ArgumentNullException.ThrowIfNull(logger);

        _inner = inner;
        _redis = redis;
        _backgroundJobs = backgroundJobs;
        _logger = logger;

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
            CachedSubscriptionEntry? deserialized = TryDeserialize(cached!);
            if (deserialized is not null)
            {
                return deserialized;
            }

            // A corrupt or legacy-format entry is treated as a miss: fall through, reload from the
            // inner repository, and overwrite the key with the current (v2) format.
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

        // Every path that changes a tenant's tier — the billing webhook (which the Stripe sync job
        // delegates to), the immediate-downgrade endpoint, the reactivate flow and the fleet-admin RPC
        // — routes its write through this decorator, so this is the one place a tier change can be
        // observed. A tier change moves the tenant's effective retention, so the surviving telemetry
        // must follow it. Status-only transitions are skipped: effective retention is a function of
        // tier and per-tenant override alone.
        if ((tier is not null) && (updated > 0))
        {
            EnqueueReclassify(tenantId);
        }

        return updated;
    }

    /// <summary>
    /// Enqueues the retention reclassification for a tenant, fire-and-forget, after the subscription
    /// write has committed. A Hangfire storage failure must never fail or roll back a subscription
    /// change that already happened, so the enqueue failure is logged and swallowed; the tenant's next
    /// plan change re-enqueues, and an operator can re-run the job from the Hangfire dashboard.
    /// </summary>
    private void EnqueueReclassify(int tenantId)
    {
        try
        {
            _backgroundJobs.Enqueue<RetentionReclassifyJob>(job => job.RunAsync(tenantId, CancellationToken.None));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to enqueue retention reclassification for tenant {TenantId}; its telemetry keeps the previous retention class until the job is re-run",
                tenantId);
        }
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

    private static CachedSubscriptionEntry? TryDeserialize(string payload)
    {
        // Returns null (a cache miss) for anything that is not a well-formed current-format entry —
        // an empty value, malformed JSON, or a null document — so a corrupt or legacy entry falls
        // through to a fresh load rather than throwing or resurrecting a stale answer.
        if (string.IsNullOrEmpty(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CachedSubscriptionEntry>(payload, JsonDefaults.CamelCase);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task InvalidateAsync(int tenantId)
    {
        IDatabase db = _redis.GetDatabase();
        await db.KeyDeleteAsync(KeyFor(tenantId));
    }
}
