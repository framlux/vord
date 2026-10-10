// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests the billable machine count: the active machine count capped at the tier's machine limit
/// and raised to the tier's floor. The tier is supplied by the caller because checkout must size the quantity for the tier
/// being purchased, not the Free tier the tenant is currently on.
/// </summary>
public sealed class SubscriptionServiceBillableCountTests
{
    private static TierFeatureLimit Limits(SubscriptionTier tier, int floor, int machineLimit = 1000) => new()
    {
        Tier = tier,
        MachineLimit = machineLimit,
        RetentionDays = 60,
        AlertRuleLimit = 10,
        WebhookLimit = 5,
        MemberLimit = 5,
        MinimumBillableMachines = floor,
    };

    [Test]
    [Arguments(SubscriptionTier.Pro, 1, 0, 1)]   // below floor -> floor
    [Arguments(SubscriptionTier.Pro, 1, 1, 1)]   // at floor
    [Arguments(SubscriptionTier.Pro, 1, 7, 7)]   // above floor -> actual
    [Arguments(SubscriptionTier.Team, 3, 0, 3)]
    [Arguments(SubscriptionTier.Team, 3, 2, 3)]
    [Arguments(SubscriptionTier.Team, 3, 3, 3)]
    [Arguments(SubscriptionTier.Team, 3, 9, 9)]
    public async Task GetBillableMachineCountAsync_AppliesTierFloor(
        SubscriptionTier tier, int floor, int active, int expected)
    {
        SubscriptionService service = BuildService(tier, floor, active);

        int result = await service.GetBillableMachineCountAsync(42, tier, CancellationToken.None);

        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task GetBillableMachineCountAsync_MissingTierRow_FallsBackToKnownFloor()
    {
        // A missing TierFeatureLimits row must not silently drop the floor to zero, which
        // would bill a paid subscription nothing.
        SubscriptionService service = BuildService(SubscriptionTier.Team, floor: 3, active: 0, tierRowMissing: true);

        int result = await service.GetBillableMachineCountAsync(42, SubscriptionTier.Team, CancellationToken.None);

        await Assert.That(result).IsEqualTo(3);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task GetBillableMachineCountAsync_EnterpriseTier_ThrowsBecauseItIsNeverAStripeQuantity(bool tierRowMissing)
    {
        // Enterprise is invoiced outside Stripe, so a billable quantity for it can only be a
        // caller bug. It must be refused whether or not its limits row exists, and must never
        // resolve to the active machine count where a caller could push it to Stripe.
        SubscriptionService service = BuildService(
            SubscriptionTier.Enterprise, floor: 0, active: 12, tierRowMissing: tierRowMissing);

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetBillableMachineCountAsync(42, SubscriptionTier.Enterprise, CancellationToken.None));

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task GetBillableMachineCountAsync_FreeTier_ThrowsRatherThanReturningTheFloor()
    {
        // Free has no Stripe subscription to size; only the Stripe-billed tiers have a quantity.
        SubscriptionService service = BuildService(SubscriptionTier.Free, floor: 0, active: 2);

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetBillableMachineCountAsync(42, SubscriptionTier.Free, CancellationToken.None));

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task GetBillableMachineCountAsync_NoneTier_ThrowsRatherThanReturningZero()
    {
        // None is not a billable tier and has no floor policy. Silently falling through to a
        // billable count of 0 would bill nothing for a subscription that should never have
        // reached this method in the first place; refuse loudly instead.
        SubscriptionService service = BuildService(SubscriptionTier.None, floor: 0, active: 5);

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetBillableMachineCountAsync(42, SubscriptionTier.None, CancellationToken.None));

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task GetBillableMachineCount_MoreHostsThanTheTierAllows_BillsOnlyTheLimit()
    {
        // A paused host is not monitored, so it must not be billed: 1,200 hosts on a 1,000-host Pro
        // plan bill 1,000.
        SubscriptionService service = BuildWithMachines(registered: 1200, tierMachineLimit: 1000, floor: 1, overrideLimit: null);

        int billable = await service.GetBillableMachineCountAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        await Assert.That(billable).IsEqualTo(1000);
    }

    [Test]
    public async Task GetBillableMachineCount_UsesTheTargetTiersLimitNotTheCurrentOne()
    {
        // Checkout passes the tier being bought. A Free tenant with 10 hosts buying Pro must be quoted
        // for all 10, because all 10 resume the moment the tier changes. The tenant is on Free, whose
        // limit is 3, so a quote taken from the current tier would come back as 3.
        SubscriptionService service = BuildWithMachines(registered: 10, tierMachineLimit: 1000, floor: 1, overrideLimit: null);

        int billable = await service.GetBillableMachineCountAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        await Assert.That(billable).IsEqualTo(10);
    }

    [Test]
    public async Task GetBillableMachineCount_OverrideBindsOnAnyTier()
    {
        SubscriptionService service = BuildWithMachines(registered: 10, tierMachineLimit: 1000, floor: 1, overrideLimit: 5);

        int billable = await service.GetBillableMachineCountAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        await Assert.That(billable).IsEqualTo(5);
    }

    [Test]
    public async Task GetBillableMachineCount_ZeroOverride_StillBillsTheFloor()
    {
        SubscriptionService service = BuildWithMachines(registered: 4, tierMachineLimit: 1000, floor: 3, overrideLimit: 0);

        int billable = await service.GetBillableMachineCountAsync(1, SubscriptionTier.Team, CancellationToken.None);

        await Assert.That(billable).IsEqualTo(3);
    }

    [Test]
    public async Task GetMachineLimitForTier_MissingTierRow_FallsBackToConfig()
    {
        SubscriptionService service = BuildWithMachines(registered: 0, tierMachineLimit: null, floor: 0, overrideLimit: null);

        int limit = await service.GetMachineLimitForTierAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        await Assert.That(limit).IsEqualTo(1000);
    }

    [Test]
    public async Task GetMachineLimitForTier_TierRowWinsOverConfig()
    {
        SubscriptionService service = BuildWithMachines(registered: 0, tierMachineLimit: 250, floor: 0, overrideLimit: null);

        int limit = await service.GetMachineLimitForTierAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        await Assert.That(limit).IsEqualTo(250);
    }

    [Test]
    public async Task GetMachineLimitForTier_OverrideWinsOverTierRow()
    {
        SubscriptionService service = BuildWithMachines(registered: 0, tierMachineLimit: 250, floor: 0, overrideLimit: 40);

        int limit = await service.GetMachineLimitForTierAsync(1, SubscriptionTier.Pro, CancellationToken.None);

        await Assert.That(limit).IsEqualTo(40);
    }

    [Test]
    public async Task GetMachineLimitForTier_AgreesWithTheLimitShownToTheCustomer()
    {
        // The pause cutoff, the bill and the limits page all read the same fold, so a tenant on the
        // tier must see exactly the number that billing and pausing use.
        SubscriptionService service = BuildWithMachines(registered: 0, tierMachineLimit: 250, floor: 0, overrideLimit: 40, currentTier: SubscriptionTier.Pro);

        int limit = await service.GetMachineLimitForTierAsync(1, SubscriptionTier.Pro, CancellationToken.None);
        EffectiveLimits shown = await service.GetEffectiveLimitsForTenantAsync(1, CancellationToken.None);

        await Assert.That(shown.MachineLimit).IsEqualTo(limit);
    }

    [Test]
    [Arguments(null, null, 7, 7)]    // nothing set -> config default
    [Arguments(null, 250, 7, 250)]   // tier row beats config
    [Arguments(40, 250, 7, 40)]      // override beats tier row
    [Arguments(0, 250, 7, 0)]        // a zero override is a real limit, not "unset"
    [Arguments(40, null, 7, 40)]     // override beats config when the tier row is missing
    public async Task ResolveLimit_OverrideThenTierRowThenConfig(int? overrideValue, int? tierRowValue, int configDefault, int expected)
    {
        int resolved = SubscriptionService.ResolveLimit(overrideValue, tierRowValue, configDefault);

        await Assert.That(resolved).IsEqualTo(expected);
    }

    /// <summary>
    /// Builds a service for tenant 1 that has <paramref name="registered"/> active machines. The tier row
    /// exists for every tier but Free, so a Free tenant resolves to the configured Free limit of 3 and a
    /// quote that wrongly used the tenant's current tier would be visibly wrong.
    /// </summary>
    private static SubscriptionService BuildWithMachines(
        int registered, int? tierMachineLimit, int floor, int? overrideLimit,
        SubscriptionTier currentTier = SubscriptionTier.Free)
    {
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetActiveMachineCountAsync(1, Arg.Any<CancellationToken>()).Returns(registered);

        ITierFeatureLimitRepository tierLimitRepo = Substitute.For<ITierFeatureLimitRepository>();
        if (tierMachineLimit is not null)
        {
            tierLimitRepo.GetLimitsForTierAsync(Arg.Is<SubscriptionTier>(t => t != SubscriptionTier.Free), Arg.Any<CancellationToken>())
                .Returns(call => Limits(call.Arg<SubscriptionTier>(), floor, tierMachineLimit.Value));
        }

        ITenantSubscriptionOverrideRepository overrideRepo = Substitute.For<ITenantSubscriptionOverrideRepository>();
        overrideRepo.GetOverrideForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(overrideLimit is null ? null : new TenantSubscriptionOverride { TenantId = 1, MachineLimit = overrideLimit });

        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        subscriptionRepo.GetSubscriptionForTenantAsync(1, Arg.Any<CancellationToken>())
            .Returns(new TenantSubscription
            {
                TenantId = 1,
                Tier = currentTier,
                Status = SubscriptionStatus.Active,
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            });

        return NewService(subscriptionRepo, machineRepo, tierLimitRepo, overrideRepo);
    }

    private static SubscriptionService BuildService(
        SubscriptionTier tier, int floor, int active, bool tierRowMissing = false)
    {
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetActiveMachineCountAsync(42, Arg.Any<CancellationToken>()).Returns(active);

        ITierFeatureLimitRepository tierLimitRepo = Substitute.For<ITierFeatureLimitRepository>();
        tierLimitRepo.GetLimitsForTierAsync(tier, Arg.Any<CancellationToken>())
            .Returns(tierRowMissing ? null : Limits(tier, floor));

        return NewService(
            Substitute.For<ISubscriptionRepository>(), machineRepo, tierLimitRepo,
            Substitute.For<ITenantSubscriptionOverrideRepository>());
    }

    private static SubscriptionService NewService(
        ISubscriptionRepository subscriptionRepo, IMachineRepository machineRepo,
        ITierFeatureLimitRepository tierLimitRepo, ITenantSubscriptionOverrideRepository overrideRepo)
    {
        IAlertRuleRepository alertRuleRepo = Substitute.For<IAlertRuleRepository>();
        IIntegrationRepository integrationRepo = Substitute.For<IIntegrationRepository>();
        ITenantRepository tenantRepo = Substitute.For<ITenantRepository>();
        IInvitationRepository invitationRepo = Substitute.For<IInvitationRepository>();

        IOptions<TierDefaultOptions> tierDefaults = Options.Create(new TierDefaultOptions
        {
            Free = new() { MachineLimit = 3, RetentionDays = 1, AlertRuleLimit = 0, WebhookLimit = 0, MemberLimit = 1 },
            Pro = new() { MachineLimit = 1000, RetentionDays = 60, AlertRuleLimit = 10, WebhookLimit = 5, MemberLimit = 5 },
            Team = new() { MachineLimit = 10000, RetentionDays = 365, AlertRuleLimit = 25, WebhookLimit = 15, MemberLimit = int.MaxValue },
        });

        return new SubscriptionService(
            subscriptionRepo, machineRepo, alertRuleRepo, integrationRepo, tierLimitRepo, overrideRepo,
            tenantRepo, invitationRepo, tierDefaults, TimeProvider.System, new NullLogger<SubscriptionService>());
    }
}
