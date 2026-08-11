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
/// Tests the billable machine count: the active machine count raised to the tier's floor.
/// The tier is supplied by the caller because checkout must size the quantity for the tier
/// being purchased, not the Free tier the tenant is currently on.
/// </summary>
public sealed class SubscriptionServiceBillableCountTests
{
    private static TierFeatureLimit Limits(SubscriptionTier tier, int floor) => new()
    {
        Tier = tier,
        MachineLimit = 1000,
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

    private static SubscriptionService BuildService(
        SubscriptionTier tier, int floor, int active, bool tierRowMissing = false)
    {
        IMachineRepository machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.GetActiveMachineCountAsync(42, Arg.Any<CancellationToken>()).Returns(active);

        ITierFeatureLimitRepository tierLimitRepo = Substitute.For<ITierFeatureLimitRepository>();
        tierLimitRepo.GetLimitsForTierAsync(tier, Arg.Any<CancellationToken>())
            .Returns(tierRowMissing ? null : Limits(tier, floor));

        ISubscriptionRepository subscriptionRepo = Substitute.For<ISubscriptionRepository>();
        IAlertRuleRepository alertRuleRepo = Substitute.For<IAlertRuleRepository>();
        IIntegrationRepository integrationRepo = Substitute.For<IIntegrationRepository>();
        ITenantSubscriptionOverrideRepository overrideRepo = Substitute.For<ITenantSubscriptionOverrideRepository>();
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
