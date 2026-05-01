// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Services.Cache;

/// <summary>
/// Tests for <see cref="DatabaseRepository"/> subscription methods: DeactivateSubscriptionAsync and SetCancelAtPeriodEndAsync.
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
    public async Task DeactivateSubscriptionAsync_ClearsCancelAtPeriodEnd()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        sub.CancelAtPeriodEnd = true;
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        DatabaseRepository cache = CreateCache(dbFactory);

        await cache.DeactivateSubscriptionAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.CancelAtPeriodEnd).IsFalse();
    }

    [Test]
    public async Task DeactivateSubscriptionAsync_ClearsPendingAction()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Team, status: SubscriptionStatus.Active);
        sub.CancelAtPeriodEnd = true;
        sub.PendingAction = PendingSubscriptionAction.DowngradeToFree;
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        DatabaseRepository cache = CreateCache(dbFactory);

        await cache.DeactivateSubscriptionAsync(1, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.PendingAction).IsEqualTo(PendingSubscriptionAction.None);
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

    // --- SetCancelAtPeriodEndAsync ---

    [Test]
    public async Task SetCancelAtPeriodEndAsync_SetsCancelAndPendingAction()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        DatabaseRepository cache = CreateCache(dbFactory);

        await cache.SetCancelAtPeriodEndAsync(
            1, true, PendingSubscriptionAction.DowngradeToFree, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.CancelAtPeriodEnd).IsTrue();
        await Assert.That(updated.PendingAction).IsEqualTo(PendingSubscriptionAction.DowngradeToFree);
    }

    [Test]
    public async Task SetCancelAtPeriodEndAsync_WithDowngradeToPro_SetsCorrectAction()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Team, status: SubscriptionStatus.Active);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        DatabaseRepository cache = CreateCache(dbFactory);

        await cache.SetCancelAtPeriodEndAsync(
            1, true, PendingSubscriptionAction.DowngradeToPro, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.CancelAtPeriodEnd).IsTrue();
        await Assert.That(updated.PendingAction).IsEqualTo(PendingSubscriptionAction.DowngradeToPro);
    }

    [Test]
    public async Task SetCancelAtPeriodEndAsync_WithCancelAccount_SetsCorrectAction()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        DatabaseRepository cache = CreateCache(dbFactory);

        await cache.SetCancelAtPeriodEndAsync(
            1, true, PendingSubscriptionAction.CancelAccount, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.CancelAtPeriodEnd).IsTrue();
        await Assert.That(updated.PendingAction).IsEqualTo(PendingSubscriptionAction.CancelAccount);
    }

    [Test]
    public async Task SetCancelAtPeriodEndAsync_ClearingCancelAndPendingAction()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        sub.CancelAtPeriodEnd = true;
        sub.PendingAction = PendingSubscriptionAction.DowngradeToFree;
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        DatabaseRepository cache = CreateCache(dbFactory);

        await cache.SetCancelAtPeriodEndAsync(
            1, false, PendingSubscriptionAction.None, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.CancelAtPeriodEnd).IsFalse();
        await Assert.That(updated.PendingAction).IsEqualTo(PendingSubscriptionAction.None);
    }

    [Test]
    public async Task SetCancelAtPeriodEndAsync_UpdatesTimestamp()
    {
        using TestDatabaseFactory dbFactory = new();
        DateTimeOffset originalTimestamp = DateTimeOffset.UtcNow.AddDays(-1);
        TenantSubscription sub = TestDataBuilder.BuildSubscription(
            tenantId: 1, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        sub.UpdatedAt = originalTimestamp;
        sub.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(sub);

        DatabaseRepository cache = CreateCache(dbFactory);

        await cache.SetCancelAtPeriodEndAsync(
            1, true, PendingSubscriptionAction.DowngradeToFree, CancellationToken.None);

        TenantSubscription? updated = await dbFactory.Context.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.TenantId == 1);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.UpdatedAt > originalTimestamp).IsTrue();
    }

    [Test]
    public async Task SetCancelAtPeriodEndAsync_NoSubscription_NoError()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseRepository cache = CreateCache(dbFactory);

        // Should not throw when no subscription exists
        await cache.SetCancelAtPeriodEndAsync(
            999, true, PendingSubscriptionAction.DowngradeToFree, CancellationToken.None);

        int count = await dbFactory.Context.TenantSubscriptions.CountAsync();
        await Assert.That(count).IsEqualTo(0);
    }
}
