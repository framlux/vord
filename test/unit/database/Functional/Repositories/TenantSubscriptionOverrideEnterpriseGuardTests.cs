// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Repository tests for the Enterprise guard on override writes: the conditional upsert and remove
/// refuse an Enterprise tenant inside the statement itself, and behave exactly like the plain ones
/// for every other tenant.
/// </summary>
public sealed class TenantSubscriptionOverrideEnterpriseGuardTests
{
    private static Database.Repositories.DatabaseRepository BuildRepository(TestDatabaseFactory dbFactory)
    {
        return new Database.Repositories.DatabaseRepository(
            dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
    }

    private static async Task<int> SeedTenantAsync(TestDatabaseFactory dbFactory, SubscriptionTier? tier = null)
    {
        Tenant tenant = TestDataBuilder.BuildTenant();
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);
        if (tier is not null)
        {
            await SeedSubscriptionAsync(dbFactory, tenantId, tier.Value);
        }

        return tenantId;
    }

    private static async Task SeedSubscriptionAsync(TestDatabaseFactory dbFactory, int tenantId, SubscriptionTier tier)
    {
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: tenantId, tier: tier, status: SubscriptionStatus.Active);
        await dbFactory.Context.InsertWithInt32IdentityAsync(sub);
    }

    private static async Task SeedOverrideAsync(TestDatabaseFactory dbFactory, int tenantId, int machineLimit)
    {
        await dbFactory.Context.InsertAsync(new TenantSubscriptionOverride
        {
            TenantId = tenantId,
            MachineLimit = machineLimit,
            RetentionDays = 30,
            AlertRuleLimit = 5,
            WebhookLimit = 4,
            MemberLimit = 3,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    [Test]
    public async Task Upsert_TeamTenantWithoutOverride_InsertsTheRow()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory, SubscriptionTier.Team);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        bool written = await repo.UpsertOverrideUnlessEnterpriseAsync(tenantId, 10, 20, 30, 40, 50, CancellationToken.None);

        TenantSubscriptionOverride? row = await repo.GetOverrideForTenantAsync(tenantId, CancellationToken.None);
        await Assert.That(written).IsTrue();
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.MachineLimit).IsEqualTo(10);
        await Assert.That(row.RetentionDays).IsEqualTo(20);
        await Assert.That(row.AlertRuleLimit).IsEqualTo(30);
        await Assert.That(row.WebhookLimit).IsEqualTo(40);
        await Assert.That(row.MemberLimit).IsEqualTo(50);
    }

    [Test]
    public async Task Upsert_TeamTenantWithOverride_ReplacesEveryLimitIncludingNulls()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory, SubscriptionTier.Team);
        await SeedOverrideAsync(dbFactory, tenantId, machineLimit: 100);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        bool written = await repo.UpsertOverrideUnlessEnterpriseAsync(tenantId, 7, null, 9, null, null, CancellationToken.None);

        TenantSubscriptionOverride? row = await repo.GetOverrideForTenantAsync(tenantId, CancellationToken.None);
        await Assert.That(written).IsTrue();
        await Assert.That(row!.MachineLimit).IsEqualTo(7);
        await Assert.That(row.RetentionDays).IsNull();
        await Assert.That(row.AlertRuleLimit).IsEqualTo(9);
        await Assert.That(row.WebhookLimit).IsNull();
        await Assert.That(row.MemberLimit).IsNull();
    }

    [Test]
    public async Task Upsert_TenantWithoutASubscription_InsertsTheRow()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        bool written = await repo.UpsertOverrideUnlessEnterpriseAsync(tenantId, 10, null, null, null, null, CancellationToken.None);

        TenantSubscriptionOverride? row = await repo.GetOverrideForTenantAsync(tenantId, CancellationToken.None);
        await Assert.That(written).IsTrue();
        await Assert.That(row!.MachineLimit).IsEqualTo(10);
    }

    [Test]
    public async Task Upsert_EnterpriseTenantWithOverride_RefusesAndLeavesTheRowUnchanged()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory, SubscriptionTier.Enterprise);
        await SeedOverrideAsync(dbFactory, tenantId, machineLimit: 500);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        bool written = await repo.UpsertOverrideUnlessEnterpriseAsync(tenantId, 1, 1, 1, 1, null, CancellationToken.None);

        TenantSubscriptionOverride? row = await repo.GetOverrideForTenantAsync(tenantId, CancellationToken.None);
        await Assert.That(written).IsFalse();
        await Assert.That(row!.MachineLimit).IsEqualTo(500);
        await Assert.That(row.RetentionDays).IsEqualTo(30);
        await Assert.That(row.MemberLimit).IsEqualTo(3);
    }

    [Test]
    public async Task Upsert_EnterpriseTenantWithoutOverride_RefusesAndInsertsNothing()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory, SubscriptionTier.Enterprise);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        bool written = await repo.UpsertOverrideUnlessEnterpriseAsync(tenantId, 10, 20, 30, 40, 50, CancellationToken.None);

        TenantSubscriptionOverride? row = await repo.GetOverrideForTenantAsync(tenantId, CancellationToken.None);
        await Assert.That(written).IsFalse();
        await Assert.That(row).IsNull();
    }

    [Test]
    public async Task Upsert_AnotherTenantBeingEnterprise_DoesNotBlockThisOne()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory, SubscriptionTier.Team);
        int otherTenantId = await SeedTenantAsync(dbFactory, SubscriptionTier.Enterprise);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        bool written = await repo.UpsertOverrideUnlessEnterpriseAsync(tenantId, 10, null, null, null, null, CancellationToken.None);

        await Assert.That(written).IsTrue();
        await Assert.That(await repo.GetOverrideForTenantAsync(tenantId, CancellationToken.None)).IsNotNull();
        await Assert.That(await repo.GetOverrideForTenantAsync(otherTenantId, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task Remove_TeamTenantWithOverride_DeletesIt()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory, SubscriptionTier.Team);
        await SeedOverrideAsync(dbFactory, tenantId, machineLimit: 100);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        bool removed = await repo.RemoveOverrideUnlessEnterpriseAsync(tenantId, CancellationToken.None);

        await Assert.That(removed).IsTrue();
        await Assert.That(await repo.GetOverrideForTenantAsync(tenantId, CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task Remove_TeamTenantWithoutOverride_IsASuccessfulNoOp()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory, SubscriptionTier.Team);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        bool removed = await repo.RemoveOverrideUnlessEnterpriseAsync(tenantId, CancellationToken.None);

        await Assert.That(removed).IsTrue();
    }

    [Test]
    public async Task Remove_EnterpriseTenant_RefusesAndKeepsTheOverride()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory, SubscriptionTier.Enterprise);
        await SeedOverrideAsync(dbFactory, tenantId, machineLimit: 500);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        bool removed = await repo.RemoveOverrideUnlessEnterpriseAsync(tenantId, CancellationToken.None);

        TenantSubscriptionOverride? row = await repo.GetOverrideForTenantAsync(tenantId, CancellationToken.None);
        await Assert.That(removed).IsFalse();
        await Assert.That(row).IsNotNull();
        await Assert.That(row!.MachineLimit).IsEqualTo(500);
    }

    [Test]
    public async Task Remove_AnotherTenantBeingEnterprise_DoesNotBlockThisOne()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory, SubscriptionTier.Team);
        int otherTenantId = await SeedTenantAsync(dbFactory, SubscriptionTier.Enterprise);
        await SeedOverrideAsync(dbFactory, tenantId, machineLimit: 100);
        await SeedOverrideAsync(dbFactory, otherTenantId, machineLimit: 500);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        bool removed = await repo.RemoveOverrideUnlessEnterpriseAsync(tenantId, CancellationToken.None);

        await Assert.That(removed).IsTrue();
        await Assert.That(await repo.GetOverrideForTenantAsync(tenantId, CancellationToken.None)).IsNull();
        await Assert.That(await repo.GetOverrideForTenantAsync(otherTenantId, CancellationToken.None)).IsNotNull();
    }

    [Test]
    public async Task UnconditionalUpsert_StillWritesAnEnterpriseTenant_ForTheAgreementHandler()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = await SeedTenantAsync(dbFactory, SubscriptionTier.Enterprise);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        await repo.UpsertOverrideAsync(tenantId, 500, 180, 40, 20, int.MaxValue, CancellationToken.None);

        TenantSubscriptionOverride? row = await repo.GetOverrideForTenantAsync(tenantId, CancellationToken.None);
        await Assert.That(row!.MachineLimit).IsEqualTo(500);
        await Assert.That(row.MemberLimit).IsEqualTo(int.MaxValue);
    }

    [Test]
    public async Task Upsert_NonexistentTenant_ThrowsRatherThanReportingAnEnterpriseRefusal()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repo.UpsertOverrideUnlessEnterpriseAsync(9999, 10, null, null, null, null, CancellationToken.None));
    }
}
