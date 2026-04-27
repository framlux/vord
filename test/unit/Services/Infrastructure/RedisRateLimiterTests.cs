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
    /// Helper to set up ScriptEvaluateAsync mock to return the given count.
    /// </summary>
    private static void MockScriptResult(IDatabase db, long returnCount)
    {
        db.ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>(),
            Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)returnCount));
    }

    /// <summary>
    /// Verifies that a request under the permit limit is allowed.
    /// </summary>
    [Test]
    public async Task IsAllowedAsync_UnderLimit_ReturnsTrue()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 10);
        MockScriptResult(db, 1L);

        bool result = await limiter.IsAllowedAsync("127.0.0.1");

        await Assert.That(result).IsTrue();
    }

    /// <summary>
    /// Verifies that a request over the permit limit is rejected.
    /// </summary>
    [Test]
    public async Task IsAllowedAsync_OverLimit_ReturnsFalse()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 10);
        MockScriptResult(db, 11L);

        bool result = await limiter.IsAllowedAsync("127.0.0.1");

        await Assert.That(result).IsFalse();
    }

    /// <summary>
    /// Verifies that the Lua script is called with the correct key and expiry argument.
    /// </summary>
    [Test]
    public async Task IsAllowedAsync_CallsScriptWithCorrectArguments()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 10);
        MockScriptResult(db, 1L);

        await limiter.IsAllowedAsync("127.0.0.1");

        await db.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]>(keys => keys.Length == 1),
            Arg.Is<RedisValue[]>(vals => vals.Length == 1),
            Arg.Any<CommandFlags>());
    }

    // ========== Boundary/edge-case tests ==========

    [Test]
    public async Task IsAllowedAsync_ExactlyAtLimit_ReturnsTrue()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 10);
        MockScriptResult(db, 10L);

        bool result = await limiter.IsAllowedAsync("127.0.0.1");

        // Boundary: count == limit is allowed (currentCount <= _permitLimit).
        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsAllowedAsync_ExactlyOneOverLimit_ReturnsFalse()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 10);
        MockScriptResult(db, 11L);

        bool result = await limiter.IsAllowedAsync("127.0.0.1");

        // Boundary: count == limit+1 is denied.
        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task AttemptAcquireCore_AlwaysReturnsFailed()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase _) = CreateLimiter(permitLimit: 10);

        // Synchronous acquire always fails to force callers through async path.
        System.Threading.RateLimiting.RateLimitLease lease = limiter.AttemptAcquire();

        await Assert.That(lease.IsAcquired).IsFalse();
    }

    [Test]
    public async Task AcquireAsyncCore_UnderLimit_ReturnsAcquiredLease()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 10);
        MockScriptResult(db, 1L);

        System.Threading.RateLimiting.RateLimitLease lease = await limiter.AcquireAsync();

        await Assert.That(lease.IsAcquired).IsTrue();
    }

    // --- Error and isolation tests ---

    /// <summary>
    /// Verifies that a Redis connection failure propagates as an exception.
    /// </summary>
    [Test]
    public async Task IsAllowedAsync_RedisConnectionFailure_PropagatesException()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 10);
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns<RedisResult>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Connection refused"));

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

        List<RedisKey[]> capturedKeys = new();
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Do<RedisKey[]>(k => capturedKeys.Add(k)), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));

        await limiter.IsAllowedAsync("192.168.1.1");
        await limiter.IsAllowedAsync("192.168.1.2");

        await Assert.That(capturedKeys.Count).IsEqualTo(2);
        await Assert.That(capturedKeys[0][0].ToString()).IsNotEqualTo(capturedKeys[1][0].ToString());
    }

    /// <summary>
    /// Verifies boundary behavior: permit limit of one allows first, denies second.
    /// </summary>
    [Test]
    public async Task IsAllowedAsync_PermitLimitOfOne_AllowsFirstDeniesSecond()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 1);

        long callCount = 0;
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(_ => RedisResult.Create((RedisValue)(++callCount)));

        bool firstResult = await limiter.IsAllowedAsync("127.0.0.1");
        bool secondResult = await limiter.IsAllowedAsync("127.0.0.1");

        await Assert.That(firstResult).IsTrue();
        await Assert.That(secondResult).IsFalse();
    }

    /// <summary>
    /// Verifies that after window expiry, Redis INCR returns 1 (key expired), resetting the count.
    /// </summary>
    [Test]
    public async Task IsAllowedAsync_WindowExpired_ResetsCount()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 2);

        // Simulate window expiry: Lua script returns 1 because the key expired and was re-created
        MockScriptResult(db, 1L);

        bool result = await limiter.IsAllowedAsync("127.0.0.1");

        // After window expiry, the counter resets and the request is allowed
        await Assert.That(result).IsTrue();
        // The Lua script handles EXPIRE atomically, so ScriptEvaluateAsync is called once
        await db.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>());
    }

    /// <summary>
    /// Verifies that AcquireAsyncCore returns rejected lease when over limit.
    /// </summary>
    [Test]
    public async Task AcquireAsyncCore_OverLimit_ReturnsRejectedLease()
    {
        (RedisFixedWindowRateLimiter limiter, IDatabase db) = CreateLimiter(permitLimit: 1);
        MockScriptResult(db, 2L);

        System.Threading.RateLimiting.RateLimitLease lease = await limiter.AcquireAsync();

        await Assert.That(lease.IsAcquired).IsFalse();
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

    // ========== RedisPartitionedRateLimiter tests ==========

    private static (RedisPartitionedRateLimiter partitioned, IDatabase db) CreatePartitionedLimiter(
        int permitLimit = 10,
        string partitionKey = "192.168.1.1")
    {
        IDatabase db = Substitute.For<IDatabase>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        RedisFixedWindowRateLimiter inner = new(redis, "ratelimit:test", permitLimit, TimeSpan.FromMinutes(1));
        RedisPartitionedRateLimiter partitioned = new(inner, partitionKey);

        return (partitioned, db);
    }

    /// <summary>
    /// Verifies that AttemptAcquireCore on the partitioned limiter returns a failed lease.
    /// </summary>
    [Test]
    public async Task Partitioned_AttemptAcquireCore_ReturnsFailedLease()
    {
        (RedisPartitionedRateLimiter partitioned, IDatabase _) = CreatePartitionedLimiter();

        System.Threading.RateLimiting.RateLimitLease lease = partitioned.AttemptAcquire();

        await Assert.That(lease.IsAcquired).IsFalse();
    }

    /// <summary>
    /// Verifies that AcquireAsyncCore on the partitioned limiter delegates to the inner limiter
    /// and returns an acquired lease when under the limit.
    /// </summary>
    [Test]
    public async Task Partitioned_AcquireAsyncCore_UnderLimit_ReturnsAcquiredLease()
    {
        (RedisPartitionedRateLimiter partitioned, IDatabase db) = CreatePartitionedLimiter(permitLimit: 10, partitionKey: "10.0.0.1");
        MockScriptResult(db, 1L);

        System.Threading.RateLimiting.RateLimitLease lease = await partitioned.AcquireAsync();

        await Assert.That(lease.IsAcquired).IsTrue();
    }

    /// <summary>
    /// Verifies that AcquireAsyncCore on the partitioned limiter returns a rejected lease
    /// when the underlying limiter is over its limit.
    /// </summary>
    [Test]
    public async Task Partitioned_AcquireAsyncCore_OverLimit_ReturnsRejectedLease()
    {
        (RedisPartitionedRateLimiter partitioned, IDatabase db) = CreatePartitionedLimiter(permitLimit: 5, partitionKey: "10.0.0.1");
        MockScriptResult(db, 6L);

        System.Threading.RateLimiting.RateLimitLease lease = await partitioned.AcquireAsync();

        await Assert.That(lease.IsAcquired).IsFalse();
    }

    /// <summary>
    /// Verifies that the partitioned limiter passes the correct partition key to the inner limiter,
    /// resulting in the key being embedded in the Redis key.
    /// </summary>
    [Test]
    public async Task Partitioned_AcquireAsyncCore_UsesPartitionKeyInRedisKey()
    {
        string expectedPartitionKey = "custom-partition-key";
        (RedisPartitionedRateLimiter partitioned, IDatabase db) = CreatePartitionedLimiter(
            permitLimit: 10,
            partitionKey: expectedPartitionKey);

        RedisKey[]? capturedKeys = null;
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Do<RedisKey[]>(k => capturedKeys = k), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));

        await partitioned.AcquireAsync();

        await Assert.That(capturedKeys).IsNotNull();
        await Assert.That(capturedKeys![0].ToString().Contains(expectedPartitionKey)).IsTrue();
    }

    /// <summary>
    /// Verifies that after the window expires (simulated by Redis returning count 1 again),
    /// the partitioned limiter allows requests again.
    /// </summary>
    [Test]
    public async Task Partitioned_WindowExpiry_NewRequestSucceeds()
    {
        (RedisPartitionedRateLimiter partitioned, IDatabase db) = CreatePartitionedLimiter(permitLimit: 1, partitionKey: "10.0.0.1");

        // Both calls return count 1 (simulating window expiry between calls)
        MockScriptResult(db, 1L);

        System.Threading.RateLimiting.RateLimitLease firstLease = await partitioned.AcquireAsync();
        await Assert.That(firstLease.IsAcquired).IsTrue();

        System.Threading.RateLimiting.RateLimitLease secondLease = await partitioned.AcquireAsync();
        await Assert.That(secondLease.IsAcquired).IsTrue();

        // The Lua script handles EXPIRE atomically, so ScriptEvaluateAsync is called twice
        await db.Received(2).ScriptEvaluateAsync(
            Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>());
    }
}
