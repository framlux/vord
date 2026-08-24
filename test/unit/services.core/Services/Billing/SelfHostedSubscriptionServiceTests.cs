// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Billing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="SelfHostedSubscriptionService"/>. A self-hosted deployment has no tiers,
/// so every entitlement question must answer permissively. The failure mode this guards is a
/// member left delegating to the inner service: the user interface shows an unlocked product while
/// a Free-tier limit still refuses the operation, with no error anywhere to explain it.
/// </summary>
public sealed class SelfHostedSubscriptionServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    private static SelfHostedSubscriptionService CreateService(out ISubscriptionService inner)
    {
        inner = Substitute.For<ISubscriptionService>();

        return new SelfHostedSubscriptionService(inner, new FakeTimeProvider(FixedNow));
    }

    [Test]
    public async Task GetSubscriptionForTenantAsync_ReturnsSyntheticActiveTeamSubscription()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(42);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Tier).IsEqualTo(SubscriptionTier.Team);
        await Assert.That(result.Status).IsEqualTo(SubscriptionStatus.Active);
        await Assert.That(result.TenantId).IsEqualTo(42);
        await inner.DidNotReceive().GetSubscriptionForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The synthetic row is serialized out of the subscription endpoint, so its timestamps are
    /// user-visible and must come from the injected clock rather than a placeholder.
    /// </summary>
    [Test]
    public async Task GetSubscriptionForTenantAsync_StampsTimestampsFromTimeProvider()
    {
        SelfHostedSubscriptionService service = CreateService(out _);

        TenantSubscription? result = await service.GetSubscriptionForTenantAsync(42);

        await Assert.That(result!.CreatedAt).IsEqualTo(FixedNow);
        await Assert.That(result.UpdatedAt).IsEqualTo(FixedNow);
    }

    [Test]
    public async Task GetEffectiveLimitsForTenantAsync_ReturnsMaxValueOnEveryField()
    {
        SelfHostedSubscriptionService service = CreateService(out _);

        EffectiveLimits limits = await service.GetEffectiveLimitsForTenantAsync(1);

        await Assert.That(limits.MachineLimit).IsEqualTo(int.MaxValue);
        await Assert.That(limits.AlertRuleLimit).IsEqualTo(int.MaxValue);
        await Assert.That(limits.WebhookLimit).IsEqualTo(int.MaxValue);
        await Assert.That(limits.MemberLimit).IsEqualTo(int.MaxValue);
        await Assert.That(limits.RetentionDays).IsEqualTo(RetentionClassPolicy.LongWindowDays);
    }

    [Test]
    public async Task CanCreateAlertRuleAsync_IsAlwaysTrue()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        inner.CanCreateAlertRuleAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);

        await Assert.That(await service.CanCreateAlertRuleAsync(1)).IsTrue();
    }

    [Test]
    public async Task CanCreateWebhookAsync_IsAlwaysTrue()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        inner.CanCreateWebhookAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);

        await Assert.That(await service.CanCreateWebhookAsync(1)).IsTrue();
    }

    [Test]
    public async Task CanAddMemberAsync_IsAlwaysTrue()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        inner.CanAddMemberAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);

        await Assert.That(await service.CanAddMemberAsync(1)).IsTrue();
    }

    /// <summary>
    /// Ingest eligibility is not an entitlement question. The real implementation checks the
    /// tenant's active flag first, which is how deactivation and pending deletion stop telemetry,
    /// so overriding it would let a deactivated tenant ingest forever. A live self-hosted tenant is
    /// eligible anyway, because eligibility accepts any Active subscription regardless of tier.
    /// </summary>
    [Test]
    public async Task IsIngestEligibleAsync_DelegatesSoTenantDeactivationStillBlocks()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        inner.IsIngestEligibleAsync(1, Arg.Any<CancellationToken>()).Returns(false);
        inner.IsIngestEligibleAsync(2, Arg.Any<CancellationToken>()).Returns(true);

        await Assert.That(await service.IsIngestEligibleAsync(1)).IsFalse();
        await Assert.That(await service.IsIngestEligibleAsync(2)).IsTrue();
    }

    /// <summary>
    /// Retention is the member most easily missed: it does not flow through EffectiveLimits, and a
    /// delegated implementation would compute one day from the real Free row, so ingested telemetry
    /// would be stamped Short and dropped after a day while the interface claimed Team.
    /// </summary>
    [Test]
    public async Task RetentionAccessors_ReturnLongWindowAndDoNotDelegate()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        inner.GetRetentionDaysForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);
        inner.GetEffectiveRetentionDaysForTenantAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);

        await Assert.That(await service.GetRetentionDaysForTenantAsync(1))
            .IsEqualTo(RetentionClassPolicy.LongWindowDays);
        await Assert.That(await service.GetEffectiveRetentionDaysForTenantAsync(1))
            .IsEqualTo(RetentionClassPolicy.LongWindowDays);
    }

    /// <summary>
    /// The retention value must classify as Long, not merely be a large number. Long is the widest
    /// retention class the partitioning scheme has; there is no unlimited class.
    /// </summary>
    [Test]
    public async Task EffectiveRetention_ClassifiesAsLong()
    {
        SelfHostedSubscriptionService service = CreateService(out _);

        int days = await service.GetEffectiveRetentionDaysForTenantAsync(1);

        await Assert.That(RetentionClassPolicy.Classify(days)).IsEqualTo(RetentionClass.Long);
    }

    /// <summary>
    /// Machine counts carry no entitlement meaning, so they must report the truth. Faking them
    /// would corrupt the dashboard and the machine-registration path.
    /// </summary>
    [Test]
    public async Task MachineCountAccessors_DelegateToInner()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        inner.GetMachineCountForTenantAsync(7, Arg.Any<CancellationToken>()).Returns(3);
        inner.GetBillableMachineCountAsync(7, SubscriptionTier.Team, Arg.Any<CancellationToken>()).Returns(3);

        await Assert.That(await service.GetMachineCountForTenantAsync(7)).IsEqualTo(3);
        await Assert.That(await service.GetBillableMachineCountAsync(7, SubscriptionTier.Team, default)).IsEqualTo(3);
        await inner.Received(1).GetMachineCountForTenantAsync(7, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetMachineCountAtDateAsync_DelegatesToInner()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        DateTimeOffset when = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        inner.GetMachineCountAtDateAsync(7, when, Arg.Any<CancellationToken>()).Returns(5);

        await Assert.That(await service.GetMachineCountAtDateAsync(7, when)).IsEqualTo(5);
    }

    /// <summary>
    /// Row provisioning must still happen: the synthetic subscription is a read-side view, and
    /// downstream code still expects a real row to exist for the tenant.
    /// </summary>
    [Test]
    public async Task ProvisioningMembers_DelegateToInner()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        TenantSubscription row = new()
        {
            TenantId = 9,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            CreatedAt = FixedNow,
            UpdatedAt = FixedNow,
        };
        inner.ProvisionFreeSubscriptionAsync(9, Arg.Any<CancellationToken>()).Returns(row);

        await Assert.That(await service.ProvisionFreeSubscriptionAsync(9)).IsEqualTo(row);

        await service.EnsureSubscriptionExistsAsync(9);
        await inner.Received(1).EnsureSubscriptionExistsAsync(9, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Constructor_NullInner_Throws()
    {
        await Assert.That(() => new SelfHostedSubscriptionService(null!, new FakeTimeProvider(FixedNow)))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullTimeProvider_Throws()
    {
        ISubscriptionService inner = Substitute.For<ISubscriptionService>();

        await Assert.That(() => new SelfHostedSubscriptionService(inner, null!))
            .Throws<ArgumentNullException>();
    }
}
