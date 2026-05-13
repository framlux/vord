// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using StackExchange.Redis;
using System.Threading.RateLimiting;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// A Redis-backed fixed window rate limiter that works across Kubernetes replicas.
/// Uses INCR + EXPIRE for atomic counter management per partition key.
/// </summary>
public sealed class RedisFixedWindowRateLimiter : RateLimiter
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _keyPrefix;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;

    /// <summary>
    /// Creates a new Redis-backed fixed window rate limiter.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="keyPrefix">Prefix for Redis keys (e.g. "ratelimit:global" or "ratelimit:login").</param>
    /// <param name="permitLimit">Maximum number of requests per window.</param>
    /// <param name="window">The time window duration.</param>
    public RedisFixedWindowRateLimiter(IConnectionMultiplexer redis, string keyPrefix, int permitLimit, TimeSpan window)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _keyPrefix = keyPrefix;
        _permitLimit = permitLimit;
        _window = window;
    }

    /// <inheritdoc/>
    public override TimeSpan? IdleDuration => null;

    /// <inheritdoc/>
    public override RateLimiterStatistics? GetStatistics()
    {
        return null;
    }

    /// <inheritdoc/>
    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        // Synchronous acquire is not supported for Redis-backed limiter.
        // Return failure to force callers through the async path.
        return new RedisRateLimitLease(false);
    }

    /// <inheritdoc/>
    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        bool allowed = await IsAllowedAsync("unknown");

        return new RedisRateLimitLease(allowed);
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
        IDatabase db = _redis.GetDatabase();

        // Compute window key based on current time bucket
        long windowId = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)_window.TotalSeconds;
        string key = $"{_keyPrefix}:{partitionKey}:{windowId}";

        int expirySeconds = (int)_window.TotalSeconds + 1;
        RedisResult result = await db.ScriptEvaluateAsync(
            IncrWithExpiryScript,
            [(RedisKey)key],
            [(RedisValue)expirySeconds]);
        long currentCount = (long)result;

        return currentCount <= _permitLimit;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        // No resources to dispose
    }

    /// <inheritdoc/>
    protected override ValueTask DisposeAsyncCore()
    {
        return ValueTask.CompletedTask;
    }

    private sealed class RedisRateLimitLease : RateLimitLease
    {
        /// <inheritdoc/>
        public override bool IsAcquired { get; }

        /// <summary>
        /// Creates a new rate limit lease.
        /// </summary>
        /// <param name="isAcquired">Whether the lease was acquired.</param>
        public RedisRateLimitLease(bool isAcquired)
        {
            IsAcquired = isAcquired;
        }

        /// <inheritdoc/>
        public override IEnumerable<string> MetadataNames => [];

        /// <inheritdoc/>
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;

            return false;
        }
    }
}
