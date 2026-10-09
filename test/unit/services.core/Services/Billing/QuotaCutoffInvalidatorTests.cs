// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="QuotaCutoffInvalidator"/>: a tenant's cached subscription and cutoff are
/// cleared once, after the write that moved them has committed, and never fail the caller.
/// </summary>
public sealed class QuotaCutoffInvalidatorTests
{
    private static async Task SeedBothKeysAsync(IConnectionMultiplexer redis, int tenantId)
    {
        IDatabase db = redis.GetDatabase();
        await db.StringSetAsync(CachingSubscriptionRepository.CacheKeyFor(tenantId), "x");
        await db.StringSetAsync(QuotaPauseService.CacheKeyFor(tenantId), "x");
    }

    private static async Task<bool> HasKeyAsync(IConnectionMultiplexer redis, string key)
    {
        RedisValue value = await redis.GetDatabase().StringGetAsync(key);

        return value.HasValue;
    }

    private static QuotaCutoffInvalidator Create(IConnectionMultiplexer redis, ILogger<QuotaCutoffInvalidator>? logger = null)
    {
        return new QuotaCutoffInvalidator(redis, logger ?? NullLogger<QuotaCutoffInvalidator>.Instance);
    }

    [Test]
    public async Task DispatchPending_ClearsBothKeysForEveryMarkedTenant_AndNothingBeforeDispatch()
    {
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        await SeedBothKeysAsync(redis, 1);
        await SeedBothKeysAsync(redis, 2);
        QuotaCutoffInvalidator invalidator = Create(redis);

        invalidator.MarkPending(1);
        invalidator.MarkPending(2);

        // Marking happens inside the writer's transaction, so nothing may be cleared yet.
        await Assert.That(await HasKeyAsync(redis, CachingSubscriptionRepository.CacheKeyFor(1))).IsTrue();
        await Assert.That(await HasKeyAsync(redis, QuotaPauseService.CacheKeyFor(1))).IsTrue();
        await Assert.That(await HasKeyAsync(redis, CachingSubscriptionRepository.CacheKeyFor(2))).IsTrue();
        await Assert.That(await HasKeyAsync(redis, QuotaPauseService.CacheKeyFor(2))).IsTrue();

        invalidator.DispatchPending();

        await Assert.That(await HasKeyAsync(redis, CachingSubscriptionRepository.CacheKeyFor(1))).IsFalse();
        await Assert.That(await HasKeyAsync(redis, QuotaPauseService.CacheKeyFor(1))).IsFalse();
        await Assert.That(await HasKeyAsync(redis, CachingSubscriptionRepository.CacheKeyFor(2))).IsFalse();
        await Assert.That(await HasKeyAsync(redis, QuotaPauseService.CacheKeyFor(2))).IsFalse();
    }

    [Test]
    public async Task DispatchPending_LeavesAnUnmarkedTenantsKeysAlone()
    {
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        await SeedBothKeysAsync(redis, 1);
        await SeedBothKeysAsync(redis, 2);
        QuotaCutoffInvalidator invalidator = Create(redis);

        invalidator.MarkPending(1);
        invalidator.DispatchPending();

        await Assert.That(await HasKeyAsync(redis, CachingSubscriptionRepository.CacheKeyFor(2))).IsTrue();
        await Assert.That(await HasKeyAsync(redis, QuotaPauseService.CacheKeyFor(2))).IsTrue();
    }

    [Test]
    public async Task Dispose_DispatchesAnythingStillPending()
    {
        // A caller that forgets to dispatch must still clear the cache at scope teardown.
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        await SeedBothKeysAsync(redis, 3);
        QuotaCutoffInvalidator invalidator = Create(redis);

        invalidator.MarkPending(3);
        invalidator.Dispose();

        await Assert.That(await HasKeyAsync(redis, CachingSubscriptionRepository.CacheKeyFor(3))).IsFalse();
        await Assert.That(await HasKeyAsync(redis, QuotaPauseService.CacheKeyFor(3))).IsFalse();
    }

    [Test]
    public async Task Dispose_CalledTwice_DispatchesOnlyOnce()
    {
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        QuotaCutoffInvalidator invalidator = Create(redis);
        invalidator.MarkPending(3);

        invalidator.Dispose();
        invalidator.MarkPending(3);
        invalidator.Dispose();

        await redis.GetDatabase().Received(1).KeyDeleteAsync(CachingSubscriptionRepository.CacheKeyFor(3), Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task DispatchPending_NothingMarked_DoesNotTouchRedis()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        QuotaCutoffInvalidator invalidator = Create(redis);

        invalidator.DispatchPending();

        await Assert.That(redis.ReceivedCalls().Count()).IsEqualTo(0);
    }

    [Test]
    public async Task DispatchPending_MarkedTwiceOrDispatchedTwice_ClearsEachKeyOnce()
    {
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        QuotaCutoffInvalidator invalidator = Create(redis);

        invalidator.MarkPending(4);
        invalidator.MarkPending(4);
        invalidator.DispatchPending();
        invalidator.DispatchPending();

        IDatabase db = redis.GetDatabase();
        await db.Received(1).KeyDeleteAsync(CachingSubscriptionRepository.CacheKeyFor(4), Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync(QuotaPauseService.CacheKeyFor(4), Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task DispatchPending_DeletesTheSubscriptionKeyBeforeTheCutoffKey_WithoutWaitingOnRedis()
    {
        // The cutoff is rebuilt from the cached subscription, so clearing it first would let a read in
        // between rebuild a stale cutoff from the old tier. Fire-and-forget keeps scope disposal from
        // blocking on Redis.
        List<(string Key, CommandFlags Flags)> deletions = [];
        IDatabase db = Substitute.For<IDatabase>();
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                deletions.Add((callInfo.ArgAt<RedisKey>(0).ToString(), callInfo.ArgAt<CommandFlags>(1)));

                return Task.FromResult(true);
            });
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        QuotaCutoffInvalidator invalidator = Create(redis);

        invalidator.MarkPending(5);
        invalidator.DispatchPending();

        await Assert.That(deletions.Count).IsEqualTo(2);
        await Assert.That(deletions[0].Key).IsEqualTo(CachingSubscriptionRepository.CacheKeyFor(5));
        await Assert.That(deletions[1].Key).IsEqualTo(QuotaPauseService.CacheKeyFor(5));
        await Assert.That(deletions.All(d => d.Flags.HasFlag(CommandFlags.FireAndForget))).IsTrue();
    }

    [Test]
    public async Task DispatchPending_RedisFailure_IsLoggedAndSwallowed()
    {
        IDatabase db = Substitute.For<IDatabase>();
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<Task<bool>>(_ => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        FakeLogger<QuotaCutoffInvalidator> logger = new();
        QuotaCutoffInvalidator invalidator = Create(redis, logger);

        invalidator.MarkPending(6);
        invalidator.DispatchPending();

        IReadOnlyList<FakeLogRecord> warnings = [.. logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Warning)];
        await Assert.That(warnings.Count).IsEqualTo(1);
        await Assert.That(warnings[0].Message).Contains("6");
    }

    [Test]
    public async Task DispatchPending_RedisTimeout_IsSwallowedToo()
    {
        // A timeout derives from TimeoutException, not RedisException, so it needs its own catch.
        IDatabase db = Substitute.For<IDatabase>();
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns<Task<bool>>(_ => throw new RedisTimeoutException("slow", CommandStatus.Unknown));
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        QuotaCutoffInvalidator invalidator = Create(redis);

        invalidator.MarkPending(7);

        await Assert.That(() => invalidator.DispatchPending()).ThrowsNothing();
    }

    [Test]
    public async Task DispatchPending_RedisFailureForOneTenant_StillClearsTheOthers()
    {
        // One tenant's failed delete must not strand the rest of the batch for 30 seconds.
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        await SeedBothKeysAsync(redis, 9);
        IDatabase db = redis.GetDatabase();
        db.KeyDeleteAsync(CachingSubscriptionRepository.CacheKeyFor(8), Arg.Any<CommandFlags>())
            .Returns<Task<bool>>(_ => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));
        QuotaCutoffInvalidator invalidator = Create(redis);

        invalidator.MarkPending(8);
        invalidator.MarkPending(9);
        invalidator.DispatchPending();

        await Assert.That(await HasKeyAsync(redis, CachingSubscriptionRepository.CacheKeyFor(9))).IsFalse();
        await Assert.That(await HasKeyAsync(redis, QuotaPauseService.CacheKeyFor(9))).IsFalse();
    }

    [Test]
    public async Task Constructor_NullArguments_Throw()
    {
        await Assert.That(() => new QuotaCutoffInvalidator(null!, NullLogger<QuotaCutoffInvalidator>.Instance))
            .Throws<ArgumentNullException>();
        await Assert.That(() => new QuotaCutoffInvalidator(FakeRedisConnection.Create(), null!))
            .Throws<ArgumentNullException>();
    }
}
