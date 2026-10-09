// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Collects the tenants whose quota cutoff may have moved during the current unit of work, and
/// clears their cached subscription and cutoff once that work has committed.
/// </summary>
/// <remarks>
/// <para>
/// Clearing inside the writer's transaction is not enough: the subscription cache is invalidated
/// mid-transaction, and any read on another connection before the commit — ingest reads it on every
/// envelope — re-seeds the old tier, from which a stale cutoff would then be rebuilt. So the seams
/// that detect a change only mark it, and the clear runs after commit: the subscription entry first,
/// then the cutoff, so the cutoff is never rebuilt from the stale tier.
/// </para>
/// <para>
/// The service is scoped and clears anything still pending when the scope is disposed, which always
/// happens after any transaction opened inside it has committed or rolled back. Clearing after a
/// rollback only costs a recomputation. The deletes are fire-and-forget so disposal never blocks on
/// Redis; the 30-second cache lifetime is the backstop if one is lost.
/// </para>
/// </remarks>
public sealed class QuotaCutoffInvalidator : IDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<QuotaCutoffInvalidator> _logger;
    private readonly HashSet<int> _pending = [];
    private bool _disposed;

    /// <summary>Creates the invalidator.</summary>
    public QuotaCutoffInvalidator(IConnectionMultiplexer redis, ILogger<QuotaCutoffInvalidator> logger)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(logger);

        _redis = redis;
        _logger = logger;
    }

    /// <summary>Records that a tenant's cutoff may have moved.</summary>
    public void MarkPending(int tenantId)
    {
        _pending.Add(tenantId);
    }

    /// <summary>Clears the cached subscription and cutoff of every marked tenant. Call after commit.</summary>
    public void DispatchPending()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        foreach (int tenantId in _pending)
        {
            try
            {
                IDatabase db = _redis.GetDatabase();
                _ = db.KeyDeleteAsync(CachingSubscriptionRepository.CacheKeyFor(tenantId), CommandFlags.FireAndForget);
                _ = db.KeyDeleteAsync(QuotaPauseService.CacheKeyFor(tenantId), CommandFlags.FireAndForget);
            }
            catch (Exception ex) when (ex is RedisException or RedisTimeoutException)
            {
                _logger.LogWarning(ex, "Could not clear the quota cutoff for tenant {TenantId}; it expires within 30 seconds", tenantId);
            }
        }

        _pending.Clear();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DispatchPending();
    }
}
