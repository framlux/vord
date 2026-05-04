// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Services.Cache;

/// <summary>
/// Tests for <see cref="DatabaseRepository"/> subscription method: DeactivateSubscriptionAsync.
/// </summary>
public class SubscriptionCacheNewMethodsTests
{
    private static DatabaseRepository CreateCache(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());
    }

    // --- DeactivateSubscriptionAsync ---

    [Test]
    public async Task DeactivateSubscriptionAsync_SetsStatusToCanceled()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        DatabaseRepository cache = CreateCache(dbFactory);

        await cache.DeactivateSubscriptionAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo(SubscriptionStatus.Canceled);
    }

    [Test]
    public async Task DeactivateSubscriptionAsync_UpdatesTimestamp()
    {
        using TestDatabaseFactory dbFactory = new();
        DateTimeOffset originalTimestamp = DateTimeOffset.UtcNow.AddDays(-1);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        sub.UpdatedAt = originalTimestamp;
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        DatabaseRepository cache = CreateCache(dbFactory);

        await cache.DeactivateSubscriptionAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.UpdatedAt > originalTimestamp).IsTrue();
    }

    [Test]
    public async Task DeactivateSubscriptionAsync_NoSubscription_NoError()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = CreateCache(dbFactory);

        // Should not throw when no subscription exists for the tenant
        await cache.DeactivateSubscriptionAsync(999, CancellationToken.None);

        int count = await dbFactory.Context.TenantSubscriptions.CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }
}
