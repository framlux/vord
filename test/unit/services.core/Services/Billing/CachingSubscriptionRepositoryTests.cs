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
        await repo.SetSubscriptionPastDueAsync(5, CancellationToken.None);
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
        await repo.UpdateSubscriptionOnCheckoutAsync(8, SubscriptionTier.Team, CancellationToken.None);
        await repo.GetSubscriptionForTenantAsync(8, CancellationToken.None);

        await inner.Received(1).UpdateSubscriptionOnCheckoutAsync(8, SubscriptionTier.Team, Arg.Any<CancellationToken>());
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
        await repo.DeactivateSubscriptionAsync(1, CancellationToken.None);

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
    public async Task Constructor_NullInner_Throws()
    {
        IConnectionMultiplexer redis = FakeRedisConnection.Create();
        IOptions<RedisOptions> options = Options.Create(new RedisOptions { ConnectionString = "x" });

        await Assert.That(() => new CachingSubscriptionRepository(null!, redis, options))
            .Throws<ArgumentNullException>();
    }
}
