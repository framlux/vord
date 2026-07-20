// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Repository tests for <c>UpdateSubscriptionStateAsync</c>, the parameterized mutator that
/// replaced the per-transition subscription update methods. Each test pins the exact column
/// semantics of one replaced mutator, including that unrelated columns stay untouched.
/// </summary>
public sealed class UpdateSubscriptionStateTests
{
    private static Database.Repositories.DatabaseRepository BuildRepository(TestDatabaseFactory dbFactory)
    {
        return new Database.Repositories.DatabaseRepository(
            dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
    }

    [Test]
    public async Task UpdateSubscriptionStateAsync_TierAndActive_MatchesCheckoutSemantics()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free, status: SubscriptionStatus.Active);
        await dbFactory.Context.InsertWithInt32IdentityAsync(sub);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        int updated = await repo.UpdateSubscriptionStateAsync(1, SubscriptionTier.Pro, SubscriptionStatus.Active);

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(1, CancellationToken.None);
        await Assert.That(updated).IsEqualTo(1);
        await Assert.That(row?.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(row?.Status).IsEqualTo(SubscriptionStatus.Active);
    }

    [Test]
    public async Task UpdateSubscriptionStateAsync_NullTier_LeavesTierUntouched()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 2, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        sub.CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(30);
        await dbFactory.Context.InsertWithInt32IdentityAsync(sub);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        int updated = await repo.UpdateSubscriptionStateAsync(2, null, SubscriptionStatus.PastDue);

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(2, CancellationToken.None);
        await Assert.That(updated).IsEqualTo(1);
        await Assert.That(row?.Tier).IsEqualTo(SubscriptionTier.Pro);
        await Assert.That(row?.Status).IsEqualTo(SubscriptionStatus.PastDue);
        await Assert.That(row?.CurrentPeriodEnd).IsNotNull();
    }

    [Test]
    public async Task UpdateSubscriptionStateAsync_ClearPeriodEnd_NullsCurrentPeriodEnd()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 3, tier: SubscriptionTier.Team, status: SubscriptionStatus.Active);
        sub.CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(30);
        await dbFactory.Context.InsertWithInt32IdentityAsync(sub);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        int updated = await repo.UpdateSubscriptionStateAsync(3, SubscriptionTier.Free, SubscriptionStatus.Active, clearCurrentPeriodEnd: true);

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(3, CancellationToken.None);
        await Assert.That(updated).IsEqualTo(1);
        await Assert.That(row?.Tier).IsEqualTo(SubscriptionTier.Free);
        await Assert.That(row?.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(row?.CurrentPeriodEnd).IsNull();
    }

    [Test]
    public async Task UpdateSubscriptionStateAsync_WithoutClearFlag_LeavesPeriodEndUntouched()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 4, tier: SubscriptionTier.Pro, status: SubscriptionStatus.PastDue);
        sub.CurrentPeriodEnd = DateTimeOffset.UtcNow.AddDays(15);
        await dbFactory.Context.InsertWithInt32IdentityAsync(sub);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        int updated = await repo.UpdateSubscriptionStateAsync(4, SubscriptionTier.Pro, SubscriptionStatus.Active);

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(4, CancellationToken.None);
        await Assert.That(updated).IsEqualTo(1);
        await Assert.That(row?.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(row?.CurrentPeriodEnd).IsNotNull();
    }

    [Test]
    public async Task UpdateSubscriptionStateAsync_UpdatesUpdatedAtTimestamp()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 5, tier: SubscriptionTier.Free, status: SubscriptionStatus.Active);
        sub.UpdatedAt = DateTimeOffset.UtcNow.AddDays(-10);
        await dbFactory.Context.InsertWithInt32IdentityAsync(sub);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        await repo.UpdateSubscriptionStateAsync(5, null, SubscriptionStatus.Canceled);

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(5, CancellationToken.None);
        await Assert.That(row).IsNotNull();
        await Assert.That(row?.Status).IsEqualTo(SubscriptionStatus.Canceled);
        await Assert.That(row!.UpdatedAt).IsGreaterThan(DateTimeOffset.UtcNow.AddDays(-1));
    }

    [Test]
    public async Task UpdateSubscriptionStateAsync_UnknownTenant_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        int updated = await repo.UpdateSubscriptionStateAsync(999999, null, SubscriptionStatus.Active);

        await Assert.That(updated).IsEqualTo(0);
    }

    [Test]
    public async Task UpdateSubscriptionStateAsync_OtherTenantRows_Untouched()
    {
        using TestDatabaseFactory dbFactory = new();
        TenantSubscription target = TestDataBuilder.BuildSubscription(tenantId: 6, tier: SubscriptionTier.Pro, status: SubscriptionStatus.Active);
        TenantSubscription other = TestDataBuilder.BuildSubscription(tenantId: 7, tier: SubscriptionTier.Team, status: SubscriptionStatus.Active);
        await dbFactory.Context.InsertWithInt32IdentityAsync(target);
        await dbFactory.Context.InsertWithInt32IdentityAsync(other);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        await repo.UpdateSubscriptionStateAsync(6, SubscriptionTier.Free, SubscriptionStatus.Canceled, clearCurrentPeriodEnd: true);

        TenantSubscription? untouched = await repo.GetSubscriptionForTenantAsync(7, CancellationToken.None);
        await Assert.That(untouched?.Tier).IsEqualTo(SubscriptionTier.Team);
        await Assert.That(untouched?.Status).IsEqualTo(SubscriptionStatus.Active);
    }
}
