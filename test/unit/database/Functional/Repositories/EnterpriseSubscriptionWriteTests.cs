// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Interceptors;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Repository tests for the Enterprise guarantees on subscription writes: every Stripe-shaped or
/// customer-shaped mutator leaves an Enterprise row alone, the paid-subscription read excludes it,
/// and the one write that enters Enterprise is revision-ordered and idempotent.
/// </summary>
public sealed class EnterpriseSubscriptionWriteTests
{
    private static readonly DateTimeOffset TermEnd = new(2027, 10, 4, 23, 59, 59, TimeSpan.Zero);

    private static Database.Repositories.DatabaseRepository BuildRepository(TestDatabaseFactory dbFactory)
    {
        return new Database.Repositories.DatabaseRepository(
            dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());
    }

    private static async Task SeedAsync(TestDatabaseFactory dbFactory, SubscriptionTier tier)
    {
        TenantSubscription sub = TestDataBuilder.BuildSubscription(tenantId: 1, tier: tier, status: SubscriptionStatus.Active);
        await dbFactory.Context.InsertWithInt32IdentityAsync(sub);
    }

    [Test]
    public async Task UpdateSubscriptionState_EnterpriseRow_IsLeftUntouched()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedAsync(dbFactory, SubscriptionTier.Enterprise);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        int updated = await repo.UpdateSubscriptionStateAsync(1, SubscriptionTier.Free, SubscriptionStatus.Canceled, clearCurrentPeriodEnd: true);

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(1, CancellationToken.None);
        await Assert.That(updated).IsEqualTo(0);
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Enterprise);
        await Assert.That(row.Status).IsEqualTo(SubscriptionStatus.Active);
    }

    [Test]
    public async Task UpdateSubscriptionState_TargetingEnterprise_ThrowsAndLeavesTheRowUnchanged()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedAsync(dbFactory, SubscriptionTier.Team);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        await Assert.That(async () => await repo.UpdateSubscriptionStateAsync(1, SubscriptionTier.Enterprise, SubscriptionStatus.Active))
            .Throws<ArgumentOutOfRangeException>();

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(1, CancellationToken.None);
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Team);
        await Assert.That(row.AppliedAgreementRevision).IsNull();
    }

    [Test]
    public async Task PeriodEndAndCancelAtPeriodEnd_EnterpriseRow_AreLeftUntouched()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedAsync(dbFactory, SubscriptionTier.Enterprise);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        int periodUpdated = await repo.UpdateSubscriptionPeriodEndAsync(1, TermEnd);
        int cancelUpdated = await repo.SetCancelAtPeriodEndAsync(1, true);

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(1, CancellationToken.None);
        await Assert.That(periodUpdated).IsEqualTo(0);
        await Assert.That(cancelUpdated).IsEqualTo(0);
        await Assert.That(row!.CancelAtPeriodEnd).IsFalse();
    }

    [Test]
    public async Task UpdateSubscriptionState_TeamRow_StillUpdates()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedAsync(dbFactory, SubscriptionTier.Team);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        int updated = await repo.UpdateSubscriptionStateAsync(1, SubscriptionTier.Pro, SubscriptionStatus.Active);

        await Assert.That(updated).IsEqualTo(1);
    }

    [Test]
    public async Task UpdateSubscriptionPeriodEnd_TeamRow_StillUpdates()
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedAsync(dbFactory, SubscriptionTier.Team);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        int updated = await repo.UpdateSubscriptionPeriodEndAsync(1, TermEnd);

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(1, CancellationToken.None);
        await Assert.That(updated).IsEqualTo(1);
        await Assert.That(row!.CurrentPeriodEnd).IsEqualTo(TermEnd);
        await Assert.That(row.Tier).IsEqualTo(SubscriptionTier.Team);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SetCancelAtPeriodEnd_TeamRow_StillUpdates(bool cancelAtPeriodEnd)
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedAsync(dbFactory, SubscriptionTier.Team);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);
        await repo.SetCancelAtPeriodEndAsync(1, cancelAtPeriodEnd == false);

        int updated = await repo.SetCancelAtPeriodEndAsync(1, cancelAtPeriodEnd);

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(1, CancellationToken.None);
        await Assert.That(updated).IsEqualTo(1);
        await Assert.That(row!.CancelAtPeriodEnd).IsEqualTo(cancelAtPeriodEnd);
        await Assert.That(row.Tier).IsEqualTo(SubscriptionTier.Team);
    }

    [Test]
    [Arguments(SubscriptionTier.Free)]
    [Arguments(SubscriptionTier.Team)]
    [Arguments(SubscriptionTier.Enterprise)]
    public async Task GetSubscriptionForUpdate_ReturnsTheCommittedRowWithoutChangingIt(SubscriptionTier tier)
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedAsync(dbFactory, tier);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        using (IDatabaseTransaction transaction = await repo.BeginTransactionAsync(CancellationToken.None))
        {
            TenantSubscription? locked = await repo.GetSubscriptionForUpdateAsync(1, CancellationToken.None);

            await Assert.That(locked).IsNotNull();
            await Assert.That(locked!.Tier).IsEqualTo(tier);
            await Assert.That(locked.Status).IsEqualTo(SubscriptionStatus.Active);
        }
    }

    [Test]
    public async Task GetSubscriptionForUpdate_NoRow_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        TenantSubscription? locked = await repo.GetSubscriptionForUpdateAsync(1, CancellationToken.None);

        await Assert.That(locked).IsNull();
    }

    [Test]
    public async Task GetPaidSubscriptions_ExcludesEnterpriseFreeAndNone()
    {
        using TestDatabaseFactory dbFactory = new();
        int tenantId = 1;
        foreach (SubscriptionTier tier in Enum.GetValues<SubscriptionTier>())
        {
            await dbFactory.Context.InsertWithInt32IdentityAsync(
                TestDataBuilder.BuildSubscription(tenantId: tenantId++, tier: tier, status: SubscriptionStatus.Active));
        }

        List<TenantSubscription> paid = await BuildRepository(dbFactory).GetPaidSubscriptionsAsync(CancellationToken.None);

        await Assert.That(paid.Select(s => s.Tier).OrderBy(t => t))
            .IsEquivalentTo([SubscriptionTier.Pro, SubscriptionTier.Team]);
    }

    [Test]
    public async Task ApplyEnterprise_NoRow_CreatesAnEnterpriseRow()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        EnterpriseApplyOutcome outcome = await repo.ApplyEnterpriseSubscriptionAsync(1, 1, TermEnd);

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(1, CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(EnterpriseApplyOutcome.Applied);
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Enterprise);
        await Assert.That(row.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(row.CurrentPeriodEnd).IsEqualTo(TermEnd);
        await Assert.That(row.CancelAtPeriodEnd).IsFalse();
        await Assert.That(row.AppliedAgreementRevision).IsEqualTo(1);
    }

    [Test]
    [Arguments(SubscriptionTier.Free)]
    [Arguments(SubscriptionTier.Team)]
    public async Task ApplyEnterprise_ExistingRow_MovesItToEnterprise(SubscriptionTier from)
    {
        using TestDatabaseFactory dbFactory = new();
        await SeedAsync(dbFactory, from);
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        EnterpriseApplyOutcome outcome = await repo.ApplyEnterpriseSubscriptionAsync(1, 1, TermEnd);

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(1, CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(EnterpriseApplyOutcome.Applied);
        await Assert.That(row!.Tier).IsEqualTo(SubscriptionTier.Enterprise);
    }

    [Test]
    public async Task ApplyEnterprise_SameRevisionTwice_IsANoOp()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);
        await repo.ApplyEnterpriseSubscriptionAsync(1, 2, TermEnd);

        EnterpriseApplyOutcome outcome = await repo.ApplyEnterpriseSubscriptionAsync(1, 2, TermEnd.AddYears(5));

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(1, CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(EnterpriseApplyOutcome.AlreadyApplied);
        await Assert.That(row!.CurrentPeriodEnd).IsEqualTo(TermEnd);
    }

    [Test]
    public async Task ApplyEnterprise_OlderRevision_IsRefusedAndChangesNothing()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);
        await repo.ApplyEnterpriseSubscriptionAsync(1, 3, TermEnd);

        EnterpriseApplyOutcome outcome = await repo.ApplyEnterpriseSubscriptionAsync(1, 2, TermEnd.AddYears(5));

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(1, CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(EnterpriseApplyOutcome.StaleRevision);
        await Assert.That(row!.AppliedAgreementRevision).IsEqualTo(3);
        await Assert.That(row.CurrentPeriodEnd).IsEqualTo(TermEnd);
    }

    /// <summary>
    /// Two deliveries of the same revision can both read the row before either writes. The one that
    /// loses the guarded update must still be told the revision is applied, because billing-api treats
    /// that as success and a stale revision as a failure to investigate.
    /// </summary>
    [Test]
    public async Task ApplyEnterprise_WhenTheSameRevisionLandedBetweenTheReadAndTheWrite_ReportsAlreadyApplied()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);
        await repo.ApplyEnterpriseSubscriptionAsync(1, 2, TermEnd);
        dbFactory.Context.AddInterceptor(new PreApplyReadInterceptor());

        EnterpriseApplyOutcome outcome = await repo.ApplyEnterpriseSubscriptionAsync(1, 2, TermEnd.AddYears(5));

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(1, CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(EnterpriseApplyOutcome.AlreadyApplied);
        await Assert.That(row!.AppliedAgreementRevision).IsEqualTo(2);
        await Assert.That(row.CurrentPeriodEnd).IsEqualTo(TermEnd);
    }

    [Test]
    public async Task ApplyEnterprise_WhenANewerRevisionLandedBetweenTheReadAndTheWrite_ReportsStaleRevision()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);
        await repo.ApplyEnterpriseSubscriptionAsync(1, 3, TermEnd);
        dbFactory.Context.AddInterceptor(new PreApplyReadInterceptor());

        EnterpriseApplyOutcome outcome = await repo.ApplyEnterpriseSubscriptionAsync(1, 2, TermEnd.AddYears(5));

        TenantSubscription? row = await repo.GetSubscriptionForTenantAsync(1, CancellationToken.None);
        await Assert.That(outcome).IsEqualTo(EnterpriseApplyOutcome.StaleRevision);
        await Assert.That(row!.AppliedAgreementRevision).IsEqualTo(3);
    }

    [Test]
    public async Task UpsertOverride_WritesMemberLimit_AndBatchReadReturnsIt()
    {
        using TestDatabaseFactory dbFactory = new();
        Database.Repositories.DatabaseRepository repo = BuildRepository(dbFactory);

        await repo.UpsertOverrideAsync(1, 500, 180, 40, 20, 75);
        await repo.UpsertOverrideAsync(2, 10, 30, 5, 5, int.MaxValue);

        List<TenantSubscriptionOverride> overrides = await repo.GetOverridesForTenantsAsync([1, 2, 3]);

        await Assert.That(overrides.Count).IsEqualTo(2);
        await Assert.That(overrides.Single(o => o.TenantId == 1).MemberLimit).IsEqualTo(75);
        await Assert.That(overrides.Single(o => o.TenantId == 2).MemberLimit).IsEqualTo(int.MaxValue);
    }

    /// <summary>
    /// Makes the first subscription read describe the row as it was before the competing apply: no
    /// agreement revision recorded. The database already holds the competitor's revision, which is
    /// exactly what a request sees when the other apply commits between its read and its write.
    /// </summary>
    private sealed class PreApplyReadInterceptor : EntityServiceInterceptor
    {
        private bool _applied;

        public override object EntityCreated(EntityCreatedEventData eventData, object entity)
        {
            if ((_applied == false) && (entity is TenantSubscription subscription))
            {
                _applied = true;
                subscription.AppliedAgreementRevision = null;
            }

            return entity;
        }
    }
}
