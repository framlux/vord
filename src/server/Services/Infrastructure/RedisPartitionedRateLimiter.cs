// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Threading.RateLimiting;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

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
