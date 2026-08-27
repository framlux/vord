// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
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
        return CreateService(out inner, out _);
    }

    private static SelfHostedSubscriptionService CreateService(out ISubscriptionService inner, out ITenantRepository tenants)
    {
        inner = Substitute.For<ISubscriptionService>();
        tenants = Substitute.For<ITenantRepository>();

        return new SelfHostedSubscriptionService(inner, tenants, new FakeTimeProvider(FixedNow));
    }

    private static Tenant ActiveTenant(int id, bool isActive)
    {
        return new Tenant
        {
            Id = id,
            Name = "Acme",
            ExternalId = $"ext-{id}",
            IsActive = isActive,
            CreatedAt = FixedNow,
            CreatedByUserId = 1,
            LogoUrl = string.Empty,
        };
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

    /// <summary>
    /// The export cooldown exists to bound a cost the hosted service pays. A self-hoster exports
    /// their own data to their own disk, so there is nothing to ration.
    /// </summary>
    [Test]
    public async Task GetDataExportCooldownAsync_IsAlwaysZero()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        inner.GetDataExportCooldownAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(TimeSpan.FromHours(24));

        await Assert.That(await service.GetDataExportCooldownAsync(1)).IsEqualTo(TimeSpan.Zero);
    }

    [Test]
    public async Task CanAddMemberAsync_IsAlwaysTrue()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner);
        inner.CanAddMemberAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(false);

        await Assert.That(await service.CanAddMemberAsync(1)).IsTrue();
    }

    /// <summary>
    /// Subscription status is a hosted-product concept. A self-hosted deployment cannot change a
    /// subscription — every billing endpoint is absent — so gating ingest on a stale or imported
    /// status would block the tenant permanently with no way to recover.
    /// </summary>
    [Test]
    public async Task IsIngestEligibleAsync_CanceledRowOnActiveTenant_IsEligible()
    {
        SelfHostedSubscriptionService service = CreateService(out ISubscriptionService inner, out ITenantRepository tenants);
        tenants.GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(ActiveTenant(1, isActive: true));
        inner.IsIngestEligibleAsync(1, Arg.Any<CancellationToken>()).Returns(false);

        await Assert.That(await service.IsIngestEligibleAsync(1)).IsTrue();
    }

    /// <summary>
    /// Unlocking entitlements must not unlock tenant deactivation: this is the enforcement point
    /// for a tenant pending deletion, and it has to survive in both deployment modes.
    /// </summary>
    [Test]
    public async Task IsIngestEligibleAsync_DeactivatedTenant_IsNotEligible()
    {
        SelfHostedSubscriptionService service = CreateService(out _, out ITenantRepository tenants);
        tenants.GetTenantByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(ActiveTenant(1, isActive: false));

        await Assert.That(await service.IsIngestEligibleAsync(1)).IsFalse();
    }

    [Test]
    public async Task IsIngestEligibleAsync_UnknownTenant_IsNotEligible()
    {
        SelfHostedSubscriptionService service = CreateService(out _, out ITenantRepository tenants);
        tenants.GetTenantByIdAsync(99, Arg.Any<CancellationToken>()).Returns((Tenant?)null);

        await Assert.That(await service.IsIngestEligibleAsync(99)).IsFalse();
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

    [Test]
    public async Task Constructor_NullInner_Throws()
    {
        ITenantRepository tenants = Substitute.For<ITenantRepository>();

        await Assert.That(() => new SelfHostedSubscriptionService(null!, tenants, new FakeTimeProvider(FixedNow)))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullTenantRepository_Throws()
    {
        ISubscriptionService inner = Substitute.For<ISubscriptionService>();

        await Assert.That(() => new SelfHostedSubscriptionService(inner, null!, new FakeTimeProvider(FixedNow)))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullTimeProvider_Throws()
    {
        ISubscriptionService inner = Substitute.For<ISubscriptionService>();
        ITenantRepository tenants = Substitute.For<ITenantRepository>();

        await Assert.That(() => new SelfHostedSubscriptionService(inner, tenants, null!))
            .Throws<ArgumentNullException>();
    }
}
