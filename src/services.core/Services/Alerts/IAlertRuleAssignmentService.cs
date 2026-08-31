// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// Writes alert-rule machine assignments, and owns the entitlement boundary that governs them.
/// </summary>
/// <remarks>
/// Assignment is reachable from both ends of the rule/machine relationship, and both ends perform the
/// same write. The boundary therefore cannot live in an endpoint: a rule stated in one handler is a
/// rule the sibling handler does not have. Every caller that changes what a rule watches goes through
/// here, so the tier question is asked once no matter which direction the request came from.
/// </remarks>
public interface IAlertRuleAssignmentService
{
    /// <summary>
    /// Replaces the machines a single rule watches, after checking that every named machine is an
    /// active machine of the tenant.
    /// </summary>
    /// <remarks>
    /// The write is bounded by the set of machines the caller says it was choosing from, because a
    /// caller that cannot see a machine cannot mean to unassign it. A machine picker draws one page
    /// of a fleet, and a fleet is not obliged to fit on a page.
    /// </remarks>
    /// <param name="rule">The rule being re-targeted, already loaded and confirmed to belong to the tenant.</param>
    /// <param name="tenantId">The tenant that owns the rule.</param>
    /// <param name="machineIds">The machines the rule should watch. Empty parks the rule without disabling it.</param>
    /// <param name="offeredMachineIds">
    /// The machines the caller was able to choose from. Only assignments inside this set may be
    /// removed; an empty set therefore removes nothing.
    /// </param>
    /// <param name="subscription">The tenant's subscription, or <c>null</c> if none exists.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What the request resolved to, and the set actually written.</returns>
    Task<RuleMachineAssignmentResult> SetMachinesForRuleAsync(
        AlertRule rule,
        int tenantId,
        IReadOnlyList<long> machineIds,
        IReadOnlyList<long> offeredMachineIds,
        TenantSubscription? subscription,
        CancellationToken ct = default);

    /// <summary>
    /// Replaces the rules a single machine is watched by.
    /// </summary>
    /// <param name="machineId">The machine being re-targeted, already confirmed to belong to the tenant.</param>
    /// <param name="tenantId">The tenant that owns the machine.</param>
    /// <param name="tenantRules">Every rule the tenant owns, used to tell built-in from custom.</param>
    /// <param name="ruleIds">The rules the caller wants the machine watched by.</param>
    /// <param name="subscription">The tenant's subscription, or <c>null</c> if none exists.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>What the request resolved to, and the set actually written.</returns>
    Task<MachineRuleAssignmentResult> SetRulesForMachineAsync(
        long machineId,
        int tenantId,
        IReadOnlyList<AlertRule> tenantRules,
        IReadOnlyList<int> ruleIds,
        TenantSubscription? subscription,
        CancellationToken ct = default);
}
