// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="CachingSubscriptionRepository"/> covering cache hit, the TTL staleness
/// bound, negative caching, and invalidation on subscription mutations.
/// </summary>
public sealed class CachingSubscriptionRepositoryTests
{
    private static TenantSubscription BuildSubscription(int tenantId, SubscriptionStatus status = SubscriptionStatus.Active)
    {
        return new TenantSubscription
        {
            Id = 1,
            TenantId = tenantId,
            Tier = SubscriptionTier.Pro,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static CachingSubscriptionRepository Create(
        ISubscriptionRepository inner,
        IConnectionMultiplexer redis,
        int ttlSeconds = 30)
    {
        IOptions<RedisOptions> options = Options.Create(new RedisOptions
        {
            ConnectionString = "localhost",
            SubscriptionCacheTtlSeconds = ttlSeconds,
        });

        return new CachingSubscriptionRepository(inner, redis, options);
    }

    [Test]
    public async Task GetSubscription_SecondCall_ServedFromCache_DoesNotHitDatabaseTwice()
    {
        ISubscriptionRepository inner = Substitute.For<ISubscriptionRepository>();
        inner.GetSubscriptionForTenantAsync(7, Arg.Any<CancellationToken>())
            .Returns(BuildSubscription(7));
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        CachingSubscriptionRepository repo = Create(inner, redis);

        TenantSubscription? first = await repo.GetSubscriptionForTenantAsync(7, CancellationToken.None);
        TenantSubscription? second = await repo.GetSubscriptionForTenantAsync(7, CancellationToken.None);

        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsNotNull();
        await Assert.That(second!.TenantId).IsEqualTo(7);

        // The database was queried exactly once; the second read came from the cache.
        await inner.Received(1).GetSubscriptionForTenantAsync(7, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetSubscription_CachesNegativeResult_DoesNotRequeryMissingTenant()
    {
        ISubscriptionRepository inner = Substitute.For<ISubscriptionRepository>();
        inner.GetSubscriptionForTenantAsync(99, Arg.Any<CancellationToken>())
            .Returns((TenantSubscription?)null);
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        CachingSubscriptionRepository repo = Create(inner, redis);

        TenantSubscription? first = await repo.GetSubscriptionForTenantAsync(99, CancellationToken.None);
        TenantSubscription? second = await repo.GetSubscriptionForTenantAsync(99, CancellationToken.None);

        await Assert.That(first).IsNull();
        await Assert.That(second).IsNull();
        await inner.Received(1).GetSubscriptionForTenantAsync(99, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetSubscription_CacheHitReturnsStoredStatus()
    {
        ISubscriptionRepository inner = Substitute.For<ISubscriptionRepository>();
        inner.GetSubscriptionForTenantAsync(3, Arg.Any<CancellationToken>())
            .Returns(BuildSubscription(3, SubscriptionStatus.Canceled));
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        CachingSubscriptionRepository repo = Create(inner, redis);

        // Prime the cache.
        await repo.GetSubscriptionForTenantAsync(3, CancellationToken.None);

        // Now have the inner repo report a different status. The cached value must be returned.
        inner.GetSubscriptionForTenantAsync(3, Arg.Any<CancellationToken>())
            .Returns(BuildSubscription(3, SubscriptionStatus.Active));

        TenantSubscription? cached = await repo.GetSubscriptionForTenantAsync(3, CancellationToken.None);

        await Assert.That(cached!.Status).IsEqualTo(SubscriptionStatus.Canceled);
    }

    [Test]
    public async Task SetSubscriptionPastDue_InvalidatesCache_NextReadHitsDatabase()
    {
        ISubscriptionRepository inner = Substitute.For<ISubscriptionRepository>();
        inner.GetSubscriptionForTenantAsync(5, Arg.Any<CancellationToken>())
            .Returns(BuildSubscription(5, SubscriptionStatus.Active));
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        CachingSubscriptionRepository repo = Create(inner, redis);

        // Prime the cache.
        await repo.GetSubscriptionForTenantAsync(5, CancellationToken.None);

        // A mutation must invalidate the cache for that tenant.
        await repo.UpdateSubscriptionStateAsync(5, null, SubscriptionStatus.PastDue, cancellationToken: CancellationToken.None);
        inner.GetSubscriptionForTenantAsync(5, Arg.Any<CancellationToken>())
            .Returns(BuildSubscription(5, SubscriptionStatus.PastDue));

        TenantSubscription? afterMutation = await repo.GetSubscriptionForTenantAsync(5, CancellationToken.None);

        await Assert.That(afterMutation!.Status).IsEqualTo(SubscriptionStatus.PastDue);
        // Two DB reads total: the initial prime and the post-invalidation read.
        await inner.Received(2).GetSubscriptionForTenantAsync(5, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateOnCheckout_InvalidatesCache()
    {
        ISubscriptionRepository inner = Substitute.For<ISubscriptionRepository>();
        inner.GetSubscriptionForTenantAsync(8, Arg.Any<CancellationToken>())
            .Returns(BuildSubscription(8));
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        CachingSubscriptionRepository repo = Create(inner, redis);

        await repo.GetSubscriptionForTenantAsync(8, CancellationToken.None);
        await repo.UpdateSubscriptionStateAsync(8, SubscriptionTier.Team, SubscriptionStatus.Active, cancellationToken: CancellationToken.None);
        await repo.GetSubscriptionForTenantAsync(8, CancellationToken.None);

        await inner.Received(1).UpdateSubscriptionStateAsync(8, SubscriptionTier.Team, SubscriptionStatus.Active, false, Arg.Any<CancellationToken>());
        await inner.Received(2).GetSubscriptionForTenantAsync(8, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MutationForOneTenant_DoesNotInvalidateAnotherTenant()
    {
        ISubscriptionRepository inner = Substitute.For<ISubscriptionRepository>();
        inner.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>()).Returns(BuildSubscription(1));
        inner.GetSubscriptionForTenantAsync(2, Arg.Any<CancellationToken>()).Returns(BuildSubscription(2));
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        CachingSubscriptionRepository repo = Create(inner, redis);

        await repo.GetSubscriptionForTenantAsync(1, CancellationToken.None);
        await repo.GetSubscriptionForTenantAsync(2, CancellationToken.None);

        // Mutate tenant 1 only.
        await repo.UpdateSubscriptionStateAsync(1, null, SubscriptionStatus.Canceled, cancellationToken: CancellationToken.None);

        // Tenant 2's cache entry must survive; reading it again must not hit the DB a second time.
        await repo.GetSubscriptionForTenantAsync(2, CancellationToken.None);

        await inner.Received(1).GetSubscriptionForTenantAsync(2, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NonPositiveTtl_FallsBackToDefault()
    {
        ISubscriptionRepository inner = Substitute.For<ISubscriptionRepository>();
        inner.GetSubscriptionForTenantAsync(4, Arg.Any<CancellationToken>()).Returns(BuildSubscription(4));
        IConnectionMultiplexer redis = FakeRedisConnection.Create();

        // A non-positive configured TTL must not throw and must still cache.
        CachingSubscriptionRepository repo = Create(inner, redis, ttlSeconds: 0);

        await repo.GetSubscriptionForTenantAsync(4, CancellationToken.None);
        await repo.GetSubscriptionForTenantAsync(4, CancellationToken.None);

        await inner.Received(1).GetSubscriptionForTenantAsync(4, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetEffectiveRetentionDays_SecondCall_ServedFromCache_DoesNotHitDatabaseTwice()
    {
        // The ingest hot path stamps every envelope with the retention class, so the effective
        // retention must be served from the cache after the first load — never a per-envelope query.
        ISubscriptionRepository inner = Substitute.For<ISubscriptionRepository>();
        inner.GetSubscriptionForTenantAsync(11, Arg.Any<CancellationToken>()).Returns(BuildSubscription(11));
        inner.GetEffectiveRetentionDaysAsync(11, Arg.Any<CancellationToken>()).Returns(60);
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        CachingSubscriptionRepository repo = Create(inner, redis);

        int first = await repo.GetEffectiveRetentionDaysAsync(11, CancellationToken.None);
        int second = await repo.GetEffectiveRetentionDaysAsync(11, CancellationToken.None);

        await Assert.That(first).IsEqualTo(60);
        await Assert.That(second).IsEqualTo(60);
        await inner.Received(1).GetEffectiveRetentionDaysAsync(11, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SubscriptionAndRetention_ShareOneCacheEntry_LoadedTogether()
    {
        // Both values live in one cache entry, so priming via a subscription read means the following
        // retention read is a cache hit — the pair is loaded from the database exactly once.
        ISubscriptionRepository inner = Substitute.For<ISubscriptionRepository>();
        inner.GetSubscriptionForTenantAsync(12, Arg.Any<CancellationToken>()).Returns(BuildSubscription(12));
        inner.GetEffectiveRetentionDaysAsync(12, Arg.Any<CancellationToken>()).Returns(365);
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        CachingSubscriptionRepository repo = Create(inner, redis);

        await repo.GetSubscriptionForTenantAsync(12, CancellationToken.None);
        int retention = await repo.GetEffectiveRetentionDaysAsync(12, CancellationToken.None);

        await Assert.That(retention).IsEqualTo(365);
        await inner.Received(1).GetSubscriptionForTenantAsync(12, Arg.Any<CancellationToken>());
        await inner.Received(1).GetEffectiveRetentionDaysAsync(12, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidateSubscriptionCache_NextRetentionReadReloadsFromDatabase()
    {
        // An override edit invalidates the cache entry; the next retention read must reflect the new
        // value within one request rather than one TTL.
        ISubscriptionRepository inner = Substitute.For<ISubscriptionRepository>();
        inner.GetSubscriptionForTenantAsync(13, Arg.Any<CancellationToken>()).Returns(BuildSubscription(13));
        inner.GetEffectiveRetentionDaysAsync(13, Arg.Any<CancellationToken>()).Returns(1);
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        CachingSubscriptionRepository repo = Create(inner, redis);

        int before = await repo.GetEffectiveRetentionDaysAsync(13, CancellationToken.None);
        await repo.InvalidateSubscriptionCacheAsync(13, CancellationToken.None);
        inner.GetEffectiveRetentionDaysAsync(13, Arg.Any<CancellationToken>()).Returns(400);
        int after = await repo.GetEffectiveRetentionDaysAsync(13, CancellationToken.None);

        await Assert.That(before).IsEqualTo(1);
        await Assert.That(after).IsEqualTo(400);
        await inner.Received(2).GetEffectiveRetentionDaysAsync(13, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Constructor_NullInner_Throws()
    {
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        IOptions<RedisOptions> options = Options.Create(new RedisOptions { ConnectionString = "x" });

        await Assert.That(() => new CachingSubscriptionRepository(null!, redis, options))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task UpdateSubscriptionState_ForwardsExactArgumentsAndReturnsInnerResult()
    {
        ISubscriptionRepository inner = Substitute.For<ISubscriptionRepository>();
        inner.UpdateSubscriptionStateAsync(6, SubscriptionTier.Free, SubscriptionStatus.Active, true, Arg.Any<CancellationToken>())
            .Returns(1);
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        CachingSubscriptionRepository repo = Create(inner, redis);

        int updated = await repo.UpdateSubscriptionStateAsync(6, SubscriptionTier.Free, SubscriptionStatus.Active, clearCurrentPeriodEnd: true, CancellationToken.None);

        await Assert.That(updated).IsEqualTo(1);
        await inner.Received(1).UpdateSubscriptionStateAsync(6, SubscriptionTier.Free, SubscriptionStatus.Active, true, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateSubscriptionState_InvalidatesTenantCacheEntry()
    {
        ISubscriptionRepository inner = Substitute.For<ISubscriptionRepository>();
        inner.GetSubscriptionForTenantAsync(9, Arg.Any<CancellationToken>()).Returns(BuildSubscription(9));
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        CachingSubscriptionRepository repo = Create(inner, redis);

        // Prime the cache, mutate, then read again — the second read must miss the cache.
        await repo.GetSubscriptionForTenantAsync(9, CancellationToken.None);
        await repo.UpdateSubscriptionStateAsync(9, null, SubscriptionStatus.Canceled, cancellationToken: CancellationToken.None);
        await repo.GetSubscriptionForTenantAsync(9, CancellationToken.None);

        await inner.Received(2).GetSubscriptionForTenantAsync(9, Arg.Any<CancellationToken>());
    }
}
