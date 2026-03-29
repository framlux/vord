// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Options;
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
    /// Checks whether a request from the given partition key is allowed.
    /// </summary>
    /// <param name="partitionKey">The partition key (e.g. IP address).</param>
    /// <returns>True if the request is allowed; false if rate limited.</returns>
    public async Task<bool> IsAllowedAsync(string partitionKey)
    {
        IDatabase db = _redis.GetDatabase();

        // Compute window key based on current time bucket
        long windowId = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)_window.TotalSeconds;
        string key = $"{_keyPrefix}:{partitionKey}:{windowId}";

        long currentCount = await db.StringIncrementAsync(key);

        // Set expiry on first increment
        if (currentCount == 1)
        {
            await db.KeyExpireAsync(key, _window.Add(TimeSpan.FromSeconds(1)));
        }

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

/// <summary>
/// Extension methods for configuring Redis-backed rate limiting.
/// </summary>
public static class RedisRateLimiterExtensions
{
    /// <summary>
    /// Configures Redis-backed rate limiting for the application, replacing in-memory rate limiting
    /// so counters are shared across Kubernetes replicas. The <see cref="IConnectionMultiplexer"/>
    /// is resolved lazily from DI, allowing tests to replace it before any connection is established.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddRedisRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });

        services.AddSingleton<IConfigureOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>>(sp =>
        {
            IConnectionMultiplexer redis = sp.GetRequiredService<IConnectionMultiplexer>();

            return new ConfigureOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>(options =>
            {
                RedisFixedWindowRateLimiter globalLimiter = new(redis, "ratelimit:global", 100, TimeSpan.FromMinutes(1));
                RedisFixedWindowRateLimiter loginLimiter = new(redis, "ratelimit:login", 10, TimeSpan.FromMinutes(5));

                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    string partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                    return RateLimitPartition.Get(partitionKey, key =>
                        new RedisPartitionedRateLimiter(globalLimiter, key));
                });

                options.AddPolicy("login", context =>
                {
                    string partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                    return RateLimitPartition.Get(partitionKey, key =>
                        new RedisPartitionedRateLimiter(loginLimiter, key));
                });
            });
        });

        return services;
    }
}

/// <summary>
/// Wraps a <see cref="RedisFixedWindowRateLimiter"/> with a specific partition key.
/// </summary>
internal sealed class RedisPartitionedRateLimiter : RateLimiter
{
    private readonly RedisFixedWindowRateLimiter _limiter;
    private readonly string _partitionKey;

    /// <summary>
    /// Creates a partitioned rate limiter wrapper.
    /// </summary>
    /// <param name="limiter">The underlying Redis rate limiter.</param>
    /// <param name="partitionKey">The partition key for this request.</param>
    public RedisPartitionedRateLimiter(RedisFixedWindowRateLimiter limiter, string partitionKey)
    {
        _limiter = limiter;
        _partitionKey = partitionKey;
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
        // Force async path
        return FailedLease.Instance;
    }

    /// <inheritdoc/>
    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        bool allowed = await _limiter.IsAllowedAsync(_partitionKey);

        return new SimpleRateLimitLease(allowed);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
    }

    /// <inheritdoc/>
    protected override ValueTask DisposeAsyncCore()
    {
        return ValueTask.CompletedTask;
    }

    private sealed class SimpleRateLimitLease : RateLimitLease
    {
        /// <inheritdoc/>
        public override bool IsAcquired { get; }

        /// <summary>
        /// Creates a rate limit lease.
        /// </summary>
        /// <param name="isAcquired">Whether the lease was acquired.</param>
        public SimpleRateLimitLease(bool isAcquired)
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

    private sealed class FailedLease : RateLimitLease
    {
        /// <summary>
        /// Shared instance of a failed lease.
        /// </summary>
        public static readonly FailedLease Instance = new();

        /// <inheritdoc/>
        public override bool IsAcquired => false;

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
