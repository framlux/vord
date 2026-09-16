// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Globalization;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// Redis-backed implementation of <see cref="IFailedSshLoginChainGate"/>. One fenced lease key per
/// machine, shared by every replica.
/// </summary>
/// <remarks>
/// Connectivity failures are deliberately propagated rather than swallowed. The rate limiter fails
/// open inside itself, which is right for abuse protection but would make an outage
/// indistinguishable from "a chain is already running" here — the caller's fail-open branch would
/// become unreachable and nothing would be scheduled at all while Redis was down. Deciding what a
/// degraded lease means, and recording it, belongs to the caller that knows the work at stake.
/// </remarks>
public sealed class RedisFailedSshLoginChainGate : IFailedSshLoginChainGate
{
    /// <summary>
    /// Extends the lease when it is unheld or held by this caller, and refuses when another chain
    /// holds it. An unheld key is reclaimed rather than refused so a chain whose lease lapsed during
    /// a Redis outage, or one opened by the ingest fail-open path, adopts the key instead of dying.
    /// </summary>
    internal const string RenewIfOwnedScript = """
        local current = redis.call("GET", KEYS[1])
        if current == false or current == ARGV[1] then
            redis.call("SET", KEYS[1], ARGV[1], "EX", ARGV[2])
            return 1
        end
        return 0
        """;

    /// <summary>
    /// Deletes the lease only when this caller still owns it, so a superseded chain cannot clear the
    /// lease of the chain that replaced it.
    /// </summary>
    internal const string ReleaseIfOwnedScript = """
        if redis.call("GET", KEYS[1]) == ARGV[1] then
            redis.call("DEL", KEYS[1])
        end
        return 1
        """;

    private readonly IConnectionMultiplexer _redis;

    /// <summary>Creates a new Redis-backed failed-SSH-login chain gate.</summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    public RedisFailedSshLoginChainGate(IConnectionMultiplexer redis)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
    }

    /// <inheritdoc/>
    public async Task<string?> TryOpenChainAsync(long machineId)
    {
        string token = Guid.NewGuid().ToString("N");

        IDatabase db = _redis.GetDatabase();
        bool acquired = await db.StringSetAsync(
            BuildKey(machineId),
            token,
            AlertConstants.FailedSshLoginChainLease,
            When.NotExists);

        return acquired ? token : null;
    }

    /// <inheritdoc/>
    public async Task<bool> RenewChainAsync(long machineId, string chainToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(chainToken);

        int leaseSeconds = (int)AlertConstants.FailedSshLoginChainLease.TotalSeconds;

        IDatabase db = _redis.GetDatabase();
        RedisResult result = await db.ScriptEvaluateAsync(
            RenewIfOwnedScript,
            [(RedisKey)BuildKey(machineId)],
            [(RedisValue)chainToken, (RedisValue)leaseSeconds]);

        return (long)result == 1L;
    }

    /// <inheritdoc/>
    public async Task ReleaseChainAsync(long machineId, string chainToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(chainToken);

        IDatabase db = _redis.GetDatabase();
        await db.ScriptEvaluateAsync(
            ReleaseIfOwnedScript,
            [(RedisKey)BuildKey(machineId)],
            [(RedisValue)chainToken]);
    }

    private static string BuildKey(long machineId)
        => $"{AlertConstants.FailedSshLoginChainKeyPrefix}:{machineId.ToString(CultureInfo.InvariantCulture)}";
}
