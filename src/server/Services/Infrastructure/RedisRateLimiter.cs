// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// A Redis-backed fixed window rate limiter that works across Kubernetes replicas.
/// Uses INCR + EXPIRE for atomic counter management per partition key.
/// </summary>
public sealed class RedisFixedWindowRateLimiter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _keyPrefix;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly ResilienceMetrics _resilienceMetrics;
    private readonly ILogger? _logger;

    /// <summary>
    /// Creates a new Redis-backed fixed window rate limiter.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="keyPrefix">Prefix for Redis keys (e.g. "ratelimit:global" or "ratelimit:login").</param>
    /// <param name="permitLimit">Maximum number of requests per window.</param>
    /// <param name="window">The time window duration.</param>
    /// <param name="resilienceMetrics">Instruments counting requests admitted without rate limiting.</param>
    /// <param name="logger">Optional logger used to warn when the limiter fails open on a Redis outage.</param>
    public RedisFixedWindowRateLimiter(IConnectionMultiplexer redis, string keyPrefix, int permitLimit, TimeSpan window, ResilienceMetrics resilienceMetrics, ILogger? logger = null)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _keyPrefix = keyPrefix;
        _permitLimit = permitLimit;
        _window = window;
        _resilienceMetrics = resilienceMetrics ?? throw new ArgumentNullException(nameof(resilienceMetrics));
        _logger = logger;
    }

    /// <summary>
    /// Lua script that atomically increments and sets expiry in a single round-trip.
    /// Returns the new count after increment.
    /// </summary>
    private const string IncrWithExpiryScript = """
        local count = redis.call("INCR", KEYS[1])
        if count == 1 then
            redis.call("EXPIRE", KEYS[1], ARGV[1])
        end
        return count
        """;

    /// <summary>
    /// Checks whether a request from the given partition key is allowed.
    /// Uses an atomic Lua script for INCR + EXPIRE to prevent race conditions.
    /// </summary>
    /// <param name="partitionKey">The partition key (e.g. IP address).</param>
    /// <returns>True if the request is allowed; false if rate limited.</returns>
    public async Task<bool> IsAllowedAsync(string partitionKey)
    {
        // Compute window key based on current time bucket
        long windowId = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)_window.TotalSeconds;
        string key = $"{_keyPrefix}:{partitionKey}:{windowId}";

        int expirySeconds = (int)_window.TotalSeconds + 1;

        try
        {
            IDatabase db = _redis.GetDatabase();
            RedisResult result = await db.ScriptEvaluateAsync(
                IncrWithExpiryScript,
                [(RedisKey)key],
                [(RedisValue)expirySeconds]);
            long currentCount = (long)result;

            return currentCount <= _permitLimit;
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            // Rate limiting is abuse protection, not a security boundary. When Redis is unreachable we
            // deliberately fail open (admit the request) rather than take the platform down; a metric and
            // warning record the degraded mode. Non-connectivity errors (script bugs) still surface.
            _resilienceMetrics.RecordFailOpen(ResilienceComponent.RateLimiter);
            _logger?.LogWarning(ex, "Redis unavailable for rate limiting on {KeyPrefix}; failing open and admitting the request", _keyPrefix);

            return true;
        }
    }
}
