// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.Hangfire;

/// <summary>
/// One-time startup task that deletes Redis keys left behind by the pre-Hangfire background
/// services and the Redis-list delivery queue. Idempotent — once the sentinel key is set, future
/// boots short-circuit. Best-effort: per-key Redis failures are logged but never bubble out, so
/// worker startup cannot be blocked by a transient Redis issue during cleanup.
/// </summary>
public sealed class LegacyRedisKeyCleanup
{
    private const string SentinelKey = "vord:legacy-cleanup:done";

    private static readonly string[] FixedKeys =
    [
        "alert:delivery:queue",
        "alert:delivery:deadletter",
        "alert-evaluation-lock",
        "lock:command-expiry",
        "lock:data-export",
        "lock:data-export-cleanup",
        "lock:state-streaming",
        "lock:partition-management",
        "lock:stripe-sync",
        "lock:usage-heartbeat",
    ];

    private static readonly string[] PatternKeys =
    [
        "alert:condition:*",
        "lock:health-sweep:*",
    ];

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<LegacyRedisKeyCleanup> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="LegacyRedisKeyCleanup"/> class.
    /// </summary>
    /// <param name="redis">Redis connection multiplexer.</param>
    /// <param name="logger">The logger.</param>
    public LegacyRedisKeyCleanup(IConnectionMultiplexer redis, ILogger<LegacyRedisKeyCleanup> logger)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(logger);

        _redis = redis;
        _logger = logger;
    }

    /// <summary>
    /// Deletes the known legacy keys (and any keys matching the known patterns) from every Redis
    /// endpoint. Short-circuits if the sentinel key indicates a prior run already completed. Per-
    /// operation Redis failures are caught and logged so worker startup is never blocked.
    /// </summary>
    /// <param name="ct">Cancellation token (provided by the host on shutdown).</param>
    public async Task RunAsync(CancellationToken ct)
    {
        IDatabase db = _redis.GetDatabase();

        // Short-circuit if a previous boot already cleaned up.
        try
        {
            if (await db.KeyExistsAsync(SentinelKey))
            {
                _logger.LogDebug("Legacy Redis key cleanup skipped — sentinel {Sentinel} already set", SentinelKey);

                return;
            }
        }
        catch (Exception ex)
        {
            // If we can't even read the sentinel, Redis is in trouble — log and bail out without
            // attempting destructive operations.
            _logger.LogWarning(ex, "Legacy Redis key cleanup sentinel check failed; skipping run");

            return;
        }

        int deleted = 0;

        foreach (string key in FixedKeys)
        {
            try
            {
                if (await db.KeyDeleteAsync(key))
                {
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete legacy Redis key {Key}; continuing", key);
            }
        }

        // Collect unique matching keys across all endpoints and patterns BEFORE deleting, so a
        // key that matches multiple patterns is deleted exactly once instead of N times per pattern.
        HashSet<string> uniqueMatches = new(StringComparer.Ordinal);
        foreach (EndPoint endpoint in _redis.GetEndPoints())
        {
            IServer server = _redis.GetServer(endpoint);
            foreach (string pattern in PatternKeys)
            {
                try
                {
                    // SCAN-based — safe on production Redis (does not block like KEYS).
                    await foreach (RedisKey key in server.KeysAsync(pattern: pattern).WithCancellation(ct))
                    {
                        uniqueMatches.Add(key.ToString());
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Legacy Redis SCAN failed for pattern {Pattern} on endpoint {Endpoint}; continuing", pattern, endpoint);
                }
            }
        }

        foreach (string key in uniqueMatches)
        {
            try
            {
                if (await db.KeyDeleteAsync(key))
                {
                    deleted++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete legacy Redis key {Key}; continuing", key);
            }
        }

        // Mark the sentinel so future boots skip this work. Best-effort — if this fails the next
        // boot will simply repeat the cleanup, which is idempotent.
        try
        {
            await db.StringSetAsync(SentinelKey, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set legacy-cleanup sentinel key {Key}; cleanup will rerun next boot", SentinelKey);
        }

        _logger.LogInformation("Legacy Redis key cleanup deleted {Count} keys", deleted);
    }
}
