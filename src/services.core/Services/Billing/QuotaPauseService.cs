// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Repositories;
using StackExchange.Redis;
using System.Globalization;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <inheritdoc/>
/// <remarks>
/// The decision is derived, never stored: a host is paused exactly when it falls outside its
/// tenant's oldest N registered machines. The cutoff is shared through Redis rather than cached per
/// process, so every replica gives an agent the same answer on ping and on ingest. Redis is only an
/// optimisation here; a Redis failure falls back to the database, and only a database failure makes
/// the decision unavailable.
/// </remarks>
public sealed class QuotaPauseService : IQuotaPauseService
{
    private const string KeyPrefix = "quota:cutoff:v1:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly ISubscriptionService _subscriptionService;
    private readonly IMachineRepository _machineRepo;
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<QuotaPauseService> _logger;

    /// <summary>Creates the service.</summary>
    public QuotaPauseService(
        ISubscriptionService subscriptionService,
        IMachineRepository machineRepo,
        IConnectionMultiplexer redis,
        ILogger<QuotaPauseService> logger)
    {
        ArgumentNullException.ThrowIfNull(subscriptionService);
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(logger);

        _subscriptionService = subscriptionService;
        _machineRepo = machineRepo;
        _redis = redis;
        _logger = logger;
    }

    /// <summary>The Redis key holding a tenant's cutoff.</summary>
    public static string CacheKeyFor(int tenantId)
    {
        return KeyPrefix + tenantId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>How many of a tenant's registered machines are paused under a limit.</summary>
    public static int PausedCount(int registered, int limit)
    {
        return limit <= 0 ? registered : Math.Max(0, registered - limit);
    }

    /// <inheritdoc/>
    public async Task<QuotaCutoff> GetCutoffAsync(int tenantId, CancellationToken ct)
    {
        QuotaCutoff? cached = await TryReadAsync(tenantId);
        if (cached is not null)
        {
            return cached.Value;
        }

        QuotaCutoff computed;
        try
        {
            int limit = (await _subscriptionService.GetEffectiveLimitsForTenantAsync(tenantId, ct)).MachineLimit;
            if (limit <= 0)
            {
                computed = QuotaCutoff.All;
            }
            else
            {
                (int _, long? cutoffId) = await _machineRepo.GetRegisteredMachineCutoffAsync(tenantId, limit, ct);
                computed = cutoffId is null ? QuotaCutoff.None : QuotaCutoff.After(cutoffId.Value);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new QuotaCheckUnavailableException(tenantId, ex);
        }

        await TryWriteAsync(tenantId, computed);

        return computed;
    }

    /// <inheritdoc/>
    public async Task<bool> IsPausedAsync(int tenantId, long machineId, CancellationToken ct)
    {
        QuotaCutoff cutoff = await GetCutoffAsync(tenantId, ct);

        return cutoff.IsPaused(machineId);
    }

    /// <inheritdoc/>
    public async Task<int> GetPausedCountAsync(int tenantId, CancellationToken ct)
    {
        try
        {
            int limit = (await _subscriptionService.GetEffectiveLimitsForTenantAsync(tenantId, ct)).MachineLimit;
            int registered = await _machineRepo.GetActiveMachineCountAsync(tenantId, ct);

            return PausedCount(registered, limit);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new QuotaCheckUnavailableException(tenantId, ex);
        }
    }

    private async Task<QuotaCutoff?> TryReadAsync(int tenantId)
    {
        RedisValue value;
        try
        {
            value = await _redis.GetDatabase().StringGetAsync(CacheKeyFor(tenantId));
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Quota cutoff cache read failed for tenant {TenantId}; computing from the database", tenantId);

            return null;
        }

        return value.HasValue ? Parse(value.ToString()) : null;
    }

    private async Task TryWriteAsync(int tenantId, QuotaCutoff cutoff)
    {
        try
        {
            await _redis.GetDatabase().StringSetAsync(CacheKeyFor(tenantId), Format(cutoff), CacheTtl);
        }
        catch (RedisException ex)
        {
            // Best-effort: the next check recomputes from the database.
            _logger.LogWarning(ex, "Quota cutoff cache write failed for tenant {TenantId}", tenantId);
        }
    }

    private static string Format(QuotaCutoff cutoff)
    {
        return cutoff.Kind switch
        {
            QuotaCutoffKind.All => "all",
            QuotaCutoffKind.After => "after:" + cutoff.CutoffId.ToString(CultureInfo.InvariantCulture),
            _ => "none",
        };
    }

    private static QuotaCutoff? Parse(string value)
    {
        if (value == "none")
        {
            return QuotaCutoff.None;
        }

        if (value == "all")
        {
            return QuotaCutoff.All;
        }

        if (value.StartsWith("after:", StringComparison.Ordinal) &&
            long.TryParse(value.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out long id))
        {
            return QuotaCutoff.After(id);
        }

        // An unreadable entry is a miss, never a guess.
        return null;
    }
}
