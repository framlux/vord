// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Alerts;

namespace Framlux.FleetManagement.UnitTest.ServicesCore.Services.Alerts;

/// <summary>
/// Tests for the pure part of <see cref="AlertRuleAssignmentService"/>: working out which rules a
/// machine should end up watched by, given the request and the tenant's entitlement.
/// </summary>
public sealed class AlertRuleAssignmentServiceTests
{
    private const int BuiltInRuleId = 10;
    private const int CustomRuleId = 20;
    private const int SecondCustomRuleId = 21;

    private static readonly List<AlertRule> TenantRules =
    [
        Rule(BuiltInRuleId, isCustom: false),
        Rule(CustomRuleId, isCustom: true),
        Rule(SecondCustomRuleId, isCustom: true),
    ];

    private static AlertRule Rule(int id, bool isCustom)
    {
        return new AlertRule
        {
            Id = id,
            TenantId = 1,
            Name = $"rule-{id}",
            Metric = AlertMetric.CpuUsage,
            Operator = AlertOperator.GreaterThan,
            Threshold = 90,
            DurationMinutes = 5,
            Severity = AlertSeverity.Warning,
            IsEnabled = true,
            IsCustom = isCustom,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
    }

    private static TenantSubscription Subscription(SubscriptionTier tier)
    {
        return new TenantSubscription
        {
            TenantId = 1,
            Tier = tier,
            Status = SubscriptionStatus.Active,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
    }

    [Test]
    public async Task Resolve_Team_WritesExactlyWhatWasRequested()
    {
        MachineRuleAssignmentResult result = AlertRuleAssignmentService.ResolveRuleSetForMachine(
            TenantRules,
            [BuiltInRuleId, CustomRuleId],
            [],
            Subscription(SubscriptionTier.Team));

        await Assert.That(result.Outcome).IsEqualTo(AlertRuleAssignmentOutcome.Applied);
        await Assert.That(result.AppliedRuleIds).Contains(BuiltInRuleId);
        await Assert.That(result.AppliedRuleIds).Contains(CustomRuleId);
    }

    [Test]
    public async Task Resolve_Team_DroppingACustomRule_RemovesIt()
    {
        // Custom rules are Team's in every respect, including the right to stop watching a machine.
        MachineRuleAssignmentResult result = AlertRuleAssignmentService.ResolveRuleSetForMachine(
            TenantRules,
            [BuiltInRuleId],
            [BuiltInRuleId, CustomRuleId],
            Subscription(SubscriptionTier.Team));

        await Assert.That(result.Outcome).IsEqualTo(AlertRuleAssignmentOutcome.Applied);
        await Assert.That(result.AppliedRuleIds).DoesNotContain(CustomRuleId);
    }

    [Test]
    public async Task Resolve_Pro_OmittingAFrozenCustomRule_CarriesItThrough()
    {
        // The machine page offers Pro no control over a custom rule, so every save from that page
        // omits it. The write is a replace-set, so a literal reading would destroy the assignment
        // rows the downgrade freeze exists to preserve.
        MachineRuleAssignmentResult result = AlertRuleAssignmentService.ResolveRuleSetForMachine(
            TenantRules,
            [BuiltInRuleId],
            [BuiltInRuleId, CustomRuleId],
            Subscription(SubscriptionTier.Pro));

        await Assert.That(result.Outcome).IsEqualTo(AlertRuleAssignmentOutcome.Applied);
        await Assert.That(result.AppliedRuleIds).Contains(BuiltInRuleId);
        await Assert.That(result.AppliedRuleIds).Contains(CustomRuleId);
    }

    [Test]
    public async Task Resolve_Pro_DroppingABuiltInRule_StillRemovesIt()
    {
        // Preserving frozen custom assignments must not turn the whole save into a no-op: enabling
        // and assigning built-ins is exactly what Pro is entitled to do.
        MachineRuleAssignmentResult result = AlertRuleAssignmentService.ResolveRuleSetForMachine(
            TenantRules,
            [],
            [BuiltInRuleId, CustomRuleId],
            Subscription(SubscriptionTier.Pro));

        await Assert.That(result.Outcome).IsEqualTo(AlertRuleAssignmentOutcome.Applied);
        await Assert.That(result.AppliedRuleIds).DoesNotContain(BuiltInRuleId);
        await Assert.That(result.AppliedRuleIds).Contains(CustomRuleId);
    }

    [Test]
    public async Task Resolve_Pro_TargetingAnUnassignedCustomRule_IsRefused()
    {
        // Putting Team-authored coverage into service is the thing Pro is not entitled to do, and it
        // is refused rather than quietly ignored.
        MachineRuleAssignmentResult result = AlertRuleAssignmentService.ResolveRuleSetForMachine(
            TenantRules,
            [BuiltInRuleId, SecondCustomRuleId],
            [BuiltInRuleId, CustomRuleId],
            Subscription(SubscriptionTier.Pro));

        await Assert.That(result.Outcome).IsEqualTo(AlertRuleAssignmentOutcome.CustomRuleRequiresTeam);
        await Assert.That(result.AppliedRuleIds.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Resolve_Pro_ReAssertingAFrozenCustomRule_IsAllowedAndChangesNothing()
    {
        // Naming a rule the machine already carries asks for no change, so there is nothing to refuse.
        MachineRuleAssignmentResult result = AlertRuleAssignmentService.ResolveRuleSetForMachine(
            TenantRules,
            [BuiltInRuleId, CustomRuleId],
            [BuiltInRuleId, CustomRuleId],
            Subscription(SubscriptionTier.Pro));

        await Assert.That(result.Outcome).IsEqualTo(AlertRuleAssignmentOutcome.Applied);
        await Assert.That(result.AppliedRuleIds).Contains(BuiltInRuleId);
        await Assert.That(result.AppliedRuleIds).Contains(CustomRuleId);
    }

    [Test]
    public async Task Resolve_NoSubscription_FreezesCustomAssignmentsToo()
    {
        // A Free tenant reads its disabled rules as an upsell; it must not be able to destroy the
        // assignment rows behind them either.
        MachineRuleAssignmentResult result = AlertRuleAssignmentService.ResolveRuleSetForMachine(
            TenantRules,
            [],
            [CustomRuleId],
            null);

        await Assert.That(result.Outcome).IsEqualTo(AlertRuleAssignmentOutcome.Applied);
        await Assert.That(result.AppliedRuleIds).Contains(CustomRuleId);
    }

    [Test]
    public async Task Resolve_Pro_DuplicateRequestedIds_AreCollapsed()
    {
        MachineRuleAssignmentResult result = AlertRuleAssignmentService.ResolveRuleSetForMachine(
            TenantRules,
            [BuiltInRuleId, BuiltInRuleId],
            [],
            Subscription(SubscriptionTier.Pro));

        await Assert.That(result.Outcome).IsEqualTo(AlertRuleAssignmentOutcome.Applied);
        await Assert.That(result.AppliedRuleIds.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Resolve_NullArguments_Throw()
    {
        await Assert.That(() => AlertRuleAssignmentService.ResolveRuleSetForMachine(null!, [], [], null))
            .Throws<ArgumentNullException>();
        await Assert.That(() => AlertRuleAssignmentService.ResolveRuleSetForMachine(TenantRules, null!, [], null))
            .Throws<ArgumentNullException>();
        await Assert.That(() => AlertRuleAssignmentService.ResolveRuleSetForMachine(TenantRules, [], null!, null))
            .Throws<ArgumentNullException>();
    }
}
