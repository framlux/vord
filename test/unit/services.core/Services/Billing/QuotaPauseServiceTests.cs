// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="QuotaPauseService"/>: the oldest N registered machines stay active, the
/// rest are paused, and the answer is shared through Redis without ever depending on it.
/// </summary>
public sealed class QuotaPauseServiceTests
{
    private static async Task<(QuotaPauseService Service, List<long> Ids, IConnectionMultiplexer Redis, TestDatabaseFactory Db, ISubscriptionService Subs)> BuildAsync(int limit, int machines)
    {
        TestDatabaseFactory dbFactory = new();
        DatabaseRepository repo = new(dbFactory.Context, new NullLogger<DatabaseRepository>());
        List<long> ids = [];
        for (int i = 0; i < machines; i++)
        {
            ids.Add(await dbFactory.Context.InsertWithInt64IdentityAsync(TestDataBuilder.BuildMachine(tenantId: 1)));
        }

        ISubscriptionService subs = Substitute.For<ISubscriptionService>();
        subs.GetEffectiveLimitsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new EffectiveLimits { MachineLimit = limit });
        IConnectionMultiplexer redis = FakeRedisConnection.Create();

        return (new QuotaPauseService(subs, repo, redis, NullLogger<QuotaPauseService>.Instance), ids, redis, dbFactory, subs);
    }

    [Test]
    public async Task OverTheLimit_PausesOnlyMachinesAfterTheLimitthOldest()
    {
        (QuotaPauseService service, List<long> ids, _, TestDatabaseFactory db, _) = await BuildAsync(limit: 3, machines: 5);
        using (db)
        {
            await Assert.That(await service.IsPausedAsync(1, ids[2], CancellationToken.None)).IsFalse();
            await Assert.That(await service.IsPausedAsync(1, ids[3], CancellationToken.None)).IsTrue();
            await Assert.That(await service.IsPausedAsync(1, ids[4], CancellationToken.None)).IsTrue();
            await Assert.That(await service.GetPausedCountAsync(1, CancellationToken.None)).IsEqualTo(2);
        }
    }

    [Test]
    public async Task ExactlyAtTheLimit_PausesNothing()
    {
        (QuotaPauseService service, _, _, TestDatabaseFactory db, _) = await BuildAsync(limit: 3, machines: 3);
        using (db)
        {
            await Assert.That(await service.GetCutoffAsync(1, CancellationToken.None)).IsEqualTo(QuotaCutoff.None);
            await Assert.That(await service.GetPausedCountAsync(1, CancellationToken.None)).IsEqualTo(0);
        }
    }

    [Test]
    public async Task ZeroLimit_PausesEverything()
    {
        (QuotaPauseService service, List<long> ids, _, TestDatabaseFactory db, _) = await BuildAsync(limit: 0, machines: 2);
        using (db)
        {
            await Assert.That(await service.GetCutoffAsync(1, CancellationToken.None)).IsEqualTo(QuotaCutoff.All);
            await Assert.That(await service.IsPausedAsync(1, ids[0], CancellationToken.None)).IsTrue();
            await Assert.That(await service.GetPausedCountAsync(1, CancellationToken.None)).IsEqualTo(2);
        }
    }

    [Test]
    public async Task SelfHosted_NeverPauses()
    {
        // Self-hosted reports int.MaxValue through the decorator; nothing may ever be paused.
        (QuotaPauseService service, List<long> ids, _, TestDatabaseFactory db, _) = await BuildAsync(limit: int.MaxValue, machines: 5);
        using (db)
        {
            await Assert.That(await service.IsPausedAsync(1, ids[4], CancellationToken.None)).IsFalse();
        }
    }

    [Test]
    public async Task TheCutoffIsCachedUnderTheVersionedKey()
    {
        (QuotaPauseService service, List<long> ids, IConnectionMultiplexer redis, TestDatabaseFactory db, ISubscriptionService subs) = await BuildAsync(limit: 3, machines: 5);
        using (db)
        {
            await service.GetCutoffAsync(1, CancellationToken.None);
            await service.GetCutoffAsync(1, CancellationToken.None);

            RedisValue cached = await redis.GetDatabase().StringGetAsync(QuotaPauseService.CacheKeyFor(1));
            await Assert.That(cached.ToString()).IsEqualTo($"after:{ids[2]}");
            await subs.Received(1).GetEffectiveLimitsForTenantAsync(1, Arg.Any<CancellationToken>());
        }
    }

    [Test]
    public async Task GetCutoffAsync_RedisThrows_ComputesFromDatabase()
    {
        // A Redis outage must not pause anyone or fail ingest: the cache is an optimisation.
        (_, List<long> ids, _, TestDatabaseFactory db, ISubscriptionService subs) = await BuildAsync(limit: 3, machines: 5);
        using (db)
        {
            IConnectionMultiplexer broken = Substitute.For<IConnectionMultiplexer>();
            IDatabase brokenDb = Substitute.For<IDatabase>();
            broken.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(brokenDb);
            brokenDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns<RedisValue>(_ => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));
            QuotaPauseService service = new(subs, new DatabaseRepository(db.Context, new NullLogger<DatabaseRepository>()), broken, NullLogger<QuotaPauseService>.Instance);

            await Assert.That(await service.IsPausedAsync(1, ids[4], CancellationToken.None)).IsTrue();
        }
    }

    [Test]
    public async Task GetCutoffAsync_RedisWriteThrows_StillReturnsTheComputedCutoff()
    {
        // The write after a miss is best-effort: a failed cache fill must not fail the decision.
        (_, List<long> ids, _, TestDatabaseFactory db, ISubscriptionService subs) = await BuildAsync(limit: 3, machines: 5);
        using (db)
        {
            IConnectionMultiplexer redis = FakeRedisConnection.Create();
            IDatabase failingWrites = Substitute.For<IDatabase>();
            failingWrites.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(RedisValue.Null);
            failingWrites.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>())
                .Returns<bool>(_ => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "down"));
            redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(failingWrites);
            QuotaPauseService service = new(subs, new DatabaseRepository(db.Context, new NullLogger<DatabaseRepository>()), redis, NullLogger<QuotaPauseService>.Instance);

            await Assert.That(await service.GetCutoffAsync(1, CancellationToken.None)).IsEqualTo(QuotaCutoff.After(ids[2]));

            // Guards against the stub binding to a different overload than production calls, which
            // would leave this test passing without the write ever being attempted.
            await failingWrites.Received(1).StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>());
        }
    }

    [Test]
    [Arguments("none", QuotaCutoffKind.None, 0L)]
    [Arguments("all", QuotaCutoffKind.All, 0L)]
    [Arguments("after:42", QuotaCutoffKind.After, 42L)]
    public async Task GetCutoffAsync_ReadsEveryCachedShape_WithoutTouchingTheDatabase(string cached, QuotaCutoffKind kind, long cutoffId)
    {
        ISubscriptionService subs = Substitute.For<ISubscriptionService>();
        IMachineRepository repo = Substitute.For<IMachineRepository>();
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        await redis.GetDatabase().StringSetAsync(QuotaPauseService.CacheKeyFor(1), cached, TimeSpan.FromSeconds(30));
        QuotaPauseService service = new(subs, repo, redis, NullLogger<QuotaPauseService>.Instance);

        QuotaCutoff cutoff = await service.GetCutoffAsync(1, CancellationToken.None);

        await Assert.That(cutoff).IsEqualTo(new QuotaCutoff(kind, cutoffId));
        await subs.DidNotReceiveWithAnyArgs().GetEffectiveLimitsForTenantAsync(default, default);
    }

    [Test]
    [Arguments("")]
    [Arguments("garbage")]
    [Arguments("after:")]
    [Arguments("after:-5")]
    [Arguments("after:12x")]
    [Arguments("All")]
    public async Task GetCutoffAsync_UnreadableCacheEntry_IsAMissNeverAGuess(string cached)
    {
        // A corrupt entry must fall back to the database rather than be read as "paused" or "none".
        (QuotaPauseService service, List<long> ids, IConnectionMultiplexer redis, TestDatabaseFactory db, _) = await BuildAsync(limit: 3, machines: 5);
        using (db)
        {
            await redis.GetDatabase().StringSetAsync(QuotaPauseService.CacheKeyFor(1), cached, TimeSpan.FromSeconds(30));

            QuotaCutoff cutoff = await service.GetCutoffAsync(1, CancellationToken.None);

            await Assert.That(cutoff).IsEqualTo(QuotaCutoff.After(ids[2]));
        }
    }

    [Test]
    public async Task DatabaseFailure_ThrowsQuotaCheckUnavailable()
    {
        ISubscriptionService subs = Substitute.For<ISubscriptionService>();
        subs.GetEffectiveLimitsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns<EffectiveLimits>(_ => throw new InvalidOperationException("db down"));
        QuotaPauseService service = new(subs, Substitute.For<IMachineRepository>(), FakeRedisConnection.Create(), NullLogger<QuotaPauseService>.Instance);

        QuotaCheckUnavailableException? ex = await Assert.ThrowsAsync<QuotaCheckUnavailableException>(() => service.IsPausedAsync(1, 9, CancellationToken.None));

        await Assert.That(ex!.TenantId).IsEqualTo(1);
    }

    [Test]
    public async Task RepositoryFailure_ThrowsQuotaCheckUnavailable()
    {
        ISubscriptionService subs = Substitute.For<ISubscriptionService>();
        subs.GetEffectiveLimitsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new EffectiveLimits { MachineLimit = 3 });
        IMachineRepository repo = Substitute.For<IMachineRepository>();
        repo.GetRegisteredMachineCutoffAsync(1, 3, Arg.Any<CancellationToken>()).Returns<(int, long?)>(_ => throw new InvalidOperationException("db down"));
        QuotaPauseService service = new(subs, repo, FakeRedisConnection.Create(), NullLogger<QuotaPauseService>.Instance);

        QuotaCheckUnavailableException? ex = await Assert.ThrowsAsync<QuotaCheckUnavailableException>(() => service.GetCutoffAsync(1, CancellationToken.None));

        await Assert.That(ex!.TenantId).IsEqualTo(1);
        await Assert.That(ex.InnerException).IsTypeOf<InvalidOperationException>();
    }

    [Test]
    public async Task GetPausedCountAsync_DatabaseFailure_ThrowsQuotaCheckUnavailable()
    {
        ISubscriptionService subs = Substitute.For<ISubscriptionService>();
        subs.GetEffectiveLimitsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(new EffectiveLimits { MachineLimit = 3 });
        IMachineRepository repo = Substitute.For<IMachineRepository>();
        repo.GetActiveMachineCountAsync(1, Arg.Any<CancellationToken>()).Returns<int>(_ => throw new InvalidOperationException("db down"));
        QuotaPauseService service = new(subs, repo, FakeRedisConnection.Create(), NullLogger<QuotaPauseService>.Instance);

        QuotaCheckUnavailableException? ex = await Assert.ThrowsAsync<QuotaCheckUnavailableException>(() => service.GetPausedCountAsync(1, CancellationToken.None));

        await Assert.That(ex!.TenantId).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_IsNotReportedAsAnUnavailableCheck()
    {
        // A cancelled request is not a failed check; callers must not treat it as "fail open".
        ISubscriptionService subs = Substitute.For<ISubscriptionService>();
        subs.GetEffectiveLimitsForTenantAsync(1, Arg.Any<CancellationToken>()).Returns<EffectiveLimits>(_ => throw new OperationCanceledException());
        QuotaPauseService service = new(subs, Substitute.For<IMachineRepository>(), FakeRedisConnection.Create(), NullLogger<QuotaPauseService>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.GetCutoffAsync(1, CancellationToken.None));
    }

    [Test]
    public async Task CacheKeyFor_IsTheVersionedPerTenantKey()
    {
        await Assert.That(QuotaPauseService.CacheKeyFor(17)).IsEqualTo("quota:cutoff:v1:17");
    }

    [Test]
    [Arguments(5, 3, 2)]
    [Arguments(3, 3, 0)]
    [Arguments(2, 0, 2)]
    [Arguments(0, 0, 0)]
    [Arguments(2, -1, 2)]
    public async Task PausedCount_FollowsTheRule(int registered, int limit, int expected)
    {
        await Assert.That(QuotaPauseService.PausedCount(registered, limit)).IsEqualTo(expected);
    }
}
