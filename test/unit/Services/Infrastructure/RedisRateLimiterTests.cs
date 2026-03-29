// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Infrastructure;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="RedisFixedWindowRateLimiter"/>.
/// </summary>
public class RedisRateLimiterTests
{
    private static (RedisFixedWindowRateLimiter limiter, IDatabase db) CreateLimiter(int permitLimit = 10)
    {
        IDatabase db = Substitute.For<IDatabase>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        RedisFixedWindowRateLimiter limiter = new(redis, "ratelimit:test", permitLimit, TimeSpan.FromMinutes(1));

        return (limiter, db);
    }

    /// <summary>
    /// Verifies that a request under the permit limit is allowed.
    /// </summary>
    [Test]
    public async Task IsAllowedAsync_UnderLimit_ReturnsTrue()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 10);
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(1L);

        bool result = await limiter.IsAllowedAsync("127.0.0.1");

        await Assert.That(result).IsEqualTo(true);
    }

    /// <summary>
    /// Verifies that a request over the permit limit is rejected.
    /// </summary>
    [Test]
    public async Task IsAllowedAsync_OverLimit_ReturnsFalse()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 10);
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(11L);

        bool result = await limiter.IsAllowedAsync("127.0.0.1");

        await Assert.That(result).IsEqualTo(false);
    }

    /// <summary>
    /// Verifies that the first increment sets a key expiry.
    /// </summary>
    [Test]
    public async Task IsAllowedAsync_FirstIncrement_SetsExpiry()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 10);
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(1L);

        await limiter.IsAllowedAsync("127.0.0.1");

        await db.Received(1).KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<ExpireWhen>(), Arg.Any<CommandFlags>());
    }

    /// <summary>
    /// Verifies that subsequent increments do not reset the key expiry.
    /// </summary>
    [Test]
    public async Task IsAllowedAsync_SubsequentIncrement_DoesNotSetExpiry()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 10);
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(2L);

        await limiter.IsAllowedAsync("127.0.0.1");

        await db.DidNotReceive().KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<ExpireWhen>(), Arg.Any<CommandFlags>());
    }

    // ========== Boundary/edge-case tests ==========

    [Test]
    public async Task IsAllowedAsync_ExactlyAtLimit_ReturnsTrue()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 10);
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(10L);

        bool result = await limiter.IsAllowedAsync("127.0.0.1");

        // Boundary: count == limit is allowed (currentCount <= _permitLimit).
        await Assert.That(result).IsEqualTo(true);
    }

    [Test]
    public async Task IsAllowedAsync_ExactlyOneOverLimit_ReturnsFalse()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 10);
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(11L);

        bool result = await limiter.IsAllowedAsync("127.0.0.1");

        // Boundary: count == limit+1 is denied.
        await Assert.That(result).IsEqualTo(false);
    }

    [Test]
    public async Task AttemptAcquireCore_AlwaysReturnsFailed()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase _) = CreateLimiter(permitLimit: 10);

        // Synchronous acquire always fails to force callers through async path.
        System.Threading.RateLimiting.RateLimitLease lease = limiter.AttemptAcquire();

        await Assert.That(lease.IsAcquired).IsEqualTo(false);
    }

    [Test]
    public async Task AcquireAsyncCore_UnderLimit_ReturnsAcquiredLease()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 10);
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(1L);

        System.Threading.RateLimiting.RateLimitLease lease = await limiter.AcquireAsync();

        await Assert.That(lease.IsAcquired).IsEqualTo(true);
    }

    // --- Error and isolation tests ---

    /// <summary>
    /// Verifies that a Redis connection failure propagates as an exception.
    /// </summary>
    [Test]
    public async Task IsAllowedAsync_RedisConnectionFailure_PropagatesException()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 10);
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns<long>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Connection refused"));

        await Assert.ThrowsAsync<RedisConnectionException>(async () =>
        {
            await limiter.IsAllowedAsync("127.0.0.1");
        });
    }

    /// <summary>
    /// Verifies that different partition keys produce different Redis keys.
    /// </summary>
    [Test]
    public async Task IsAllowedAsync_DifferentPartitionKeys_IndependentCounters()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 10);

        List<RedisKey> capturedKeys = new();
        db.StringIncrementAsync(Arg.Do<RedisKey>(k => capturedKeys.Add(k)), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(1L);

        await limiter.IsAllowedAsync("192.168.1.1");
        await limiter.IsAllowedAsync("192.168.1.2");

        await Assert.That(capturedKeys.Count).IsEqualTo(2);
        await Assert.That(capturedKeys[0].ToString()).IsNotEqualTo(capturedKeys[1].ToString());
    }

    /// <summary>
    /// Verifies boundary behavior: permit limit of one allows first, denies second.
    /// </summary>
    [Test]
    public async Task IsAllowedAsync_PermitLimitOfOne_AllowsFirstDeniesSecond()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 1);

        long callCount = 0;
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(_ => ++callCount);

        bool firstResult = await limiter.IsAllowedAsync("127.0.0.1");
        bool secondResult = await limiter.IsAllowedAsync("127.0.0.1");

        await Assert.That(firstResult).IsEqualTo(true);
        await Assert.That(secondResult).IsEqualTo(false);
    }

    /// <summary>
    /// Verifies that constructor rejects null Redis connection.
    /// </summary>
    [Test]
    public async Task Constructor_NullRedis_ThrowsArgumentNullException()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            RedisFixedWindowRateLimiter _ = new(null!, "ratelimit:test", 10, TimeSpan.FromMinutes(1));

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("redis");
    }
}
