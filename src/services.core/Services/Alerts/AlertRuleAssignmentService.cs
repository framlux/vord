// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <inheritdoc/>
public sealed class AlertRuleAssignmentService : IAlertRuleAssignmentService
{
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IMachineRepository _machineRepo;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertRuleAssignmentService"/> class.
    /// </summary>
    /// <param name="alertRuleRepo">Alert rule repository.</param>
    /// <param name="machineRepo">Machine repository.</param>
    public AlertRuleAssignmentService(IAlertRuleRepository alertRuleRepo, IMachineRepository machineRepo)
    {
        ArgumentNullException.ThrowIfNull(alertRuleRepo);
        ArgumentNullException.ThrowIfNull(machineRepo);

        _alertRuleRepo = alertRuleRepo;
        _machineRepo = machineRepo;
    }

    /// <inheritdoc/>
    public async Task<RuleMachineAssignmentResult> SetMachinesForRuleAsync(
        AlertRule rule,
        int tenantId,
        IReadOnlyList<long> machineIds,
        IReadOnlyList<long> offeredMachineIds,
        TenantSubscription? subscription,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(machineIds);
        ArgumentNullException.ThrowIfNull(offeredMachineIds);

        // A Pro downgrade freezes custom rules exactly as they were — assignments intact, rule
        // disabled — so re-targeting one would put Team-authored coverage back into service on a Pro
        // plan just as surely as switching it back on would.
        if (rule.IsCustom && SubscriptionPolicy.RequiresTeam(subscription))
        {
            return new RuleMachineAssignmentResult { Outcome = AlertRuleAssignmentOutcome.CustomRuleRequiresTeam };
        }

        // The repository silently drops machine ids that do not belong to the tenant, so a request
        // naming another tenant's machine would otherwise report success having assigned fewer
        // machines than asked for. The rejection has to happen before the write.
        List<long> validMachineIds = await _machineRepo.GetActiveMachineIdsForTenantAsync(tenantId, machineIds, ct);
        if (validMachineIds.Count != machineIds.Distinct().Count())
        {
            return new RuleMachineAssignmentResult { Outcome = AlertRuleAssignmentOutcome.InvalidMachineIds };
        }

        Dictionary<int, List<long>> currentByRule = await _alertRuleRepo.GetMachineIdsForRulesAsync([rule.Id], ct);
        IReadOnlyList<long> effective = ResolveMachineSetForRule(
            machineIds, offeredMachineIds, currentByRule[rule.Id]);

        bool assigned = await _alertRuleRepo.SetMachinesForRuleAsync(rule.Id, tenantId, effective, ct);
        if (assigned == false)
        {
            return new RuleMachineAssignmentResult { Outcome = AlertRuleAssignmentOutcome.NotFound };
        }

        return new RuleMachineAssignmentResult
        {
            Outcome = AlertRuleAssignmentOutcome.Applied,
            AppliedMachineIds = effective,
        };
    }

    /// <summary>
    /// Works out which machines a rule should end up watching, given what the caller asked for and
    /// which machines it was able to ask about.
    /// </summary>
    /// <remarks>
    /// The rule-side write is a replace-set, so whatever this returns is the rule's complete
    /// assignment list afterwards. A picker draws one page of the fleet, and a machine it never drew
    /// is a machine the caller never unchecked — reading that silence as a removal deletes coverage
    /// nobody asked to delete, and reports success for it. The offered set is therefore what bounds
    /// the removal: assignments outside it are carried through. It bounds removals only, so a machine
    /// the caller names is assigned whether it was offered or not.
    /// </remarks>
    /// <param name="requestedMachineIds">The machines the caller wants the rule to watch.</param>
    /// <param name="offeredMachineIds">The machines the caller was able to choose from.</param>
    /// <param name="currentMachineIds">The machines the rule watches right now.</param>
    /// <returns>The set to write.</returns>
    public static IReadOnlyList<long> ResolveMachineSetForRule(
        IReadOnlyList<long> requestedMachineIds,
        IReadOnlyList<long> offeredMachineIds,
        IReadOnlyList<long> currentMachineIds)
    {
        ArgumentNullException.ThrowIfNull(requestedMachineIds);
        ArgumentNullException.ThrowIfNull(offeredMachineIds);
        ArgumentNullException.ThrowIfNull(currentMachineIds);

        HashSet<long> offered = [.. offeredMachineIds];

        return [.. requestedMachineIds
            .Concat(currentMachineIds.Where(id => offered.Contains(id) == false))
            .Distinct()];
    }

    /// <inheritdoc/>
    public async Task<MachineRuleAssignmentResult> SetRulesForMachineAsync(
        long machineId,
        int tenantId,
        IReadOnlyList<AlertRule> tenantRules,
        IReadOnlyList<int> ruleIds,
        TenantSubscription? subscription,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tenantRules);
        ArgumentNullException.ThrowIfNull(ruleIds);

        List<int> currentRuleIds = await _alertRuleRepo.GetRuleIdsForMachineAsync(machineId, tenantId, ct);

        MachineRuleAssignmentResult resolved = ResolveRuleSetForMachine(tenantRules, ruleIds, currentRuleIds, subscription);
        if (resolved.Outcome != AlertRuleAssignmentOutcome.Applied)
        {
            return resolved;
        }

        bool assigned = await _alertRuleRepo.SetRulesForMachineAsync(machineId, tenantId, resolved.AppliedRuleIds, ct);
        if (assigned == false)
        {
            return new MachineRuleAssignmentResult { Outcome = AlertRuleAssignmentOutcome.NotFound };
        }

        return resolved;
    }

    /// <summary>
    /// Works out which rules a machine should end up assigned to, given what the caller asked for and
    /// what the tenant is entitled to.
    /// </summary>
    /// <remarks>
    /// The machine-side write is a replace-set, not a merge: whatever this returns is the machine's
    /// complete assignment list afterwards. Below Team that makes an omission dangerous, because a
    /// custom rule frozen by a downgrade is offered by no control the tenant can see, so every save
    /// from that page omits it — and an omission taken literally would destroy the assignment rows the
    /// freeze exists to preserve. The requested set is therefore read as covering built-in rules only,
    /// with the machine's existing custom assignments carried through. Naming a custom rule the machine
    /// does not already carry is the opposite case: that is an attempt to put Team-authored coverage
    /// into service, and it is refused rather than quietly ignored.
    /// </remarks>
    /// <param name="tenantRules">Every rule the tenant owns, used to tell built-in from custom.</param>
    /// <param name="requestedRuleIds">The rules the caller wants the machine watched by.</param>
    /// <param name="currentRuleIds">The rules the machine is watched by right now.</param>
    /// <param name="subscription">The tenant's subscription, or <c>null</c> if none exists.</param>
    /// <returns>The set to write, or the refusal that stops the write happening.</returns>
    public static MachineRuleAssignmentResult ResolveRuleSetForMachine(
        IReadOnlyList<AlertRule> tenantRules,
        IReadOnlyList<int> requestedRuleIds,
        IReadOnlyList<int> currentRuleIds,
        TenantSubscription? subscription)
    {
        ArgumentNullException.ThrowIfNull(tenantRules);
        ArgumentNullException.ThrowIfNull(requestedRuleIds);
        ArgumentNullException.ThrowIfNull(currentRuleIds);

        List<int> requested = requestedRuleIds.Distinct().ToList();

        if (SubscriptionPolicy.RequiresTeam(subscription) == false)
        {
            return new MachineRuleAssignmentResult
            {
                Outcome = AlertRuleAssignmentOutcome.Applied,
                AppliedRuleIds = requested,
            };
        }

        HashSet<int> customRuleIds = tenantRules.Where(r => r.IsCustom).Select(r => r.Id).ToHashSet();
        HashSet<int> frozenRuleIds = currentRuleIds.Where(customRuleIds.Contains).ToHashSet();

        bool targetsNewCustomRule = requested.Any(id => customRuleIds.Contains(id) && (frozenRuleIds.Contains(id) == false));
        if (targetsNewCustomRule)
        {
            return new MachineRuleAssignmentResult { Outcome = AlertRuleAssignmentOutcome.CustomRuleRequiresTeam };
        }

        List<int> effective = requested
            .Where(id => customRuleIds.Contains(id) == false)
            .Concat(frozenRuleIds)
            .Distinct()
            .ToList();

        return new MachineRuleAssignmentResult
        {
            Outcome = AlertRuleAssignmentOutcome.Applied,
            AppliedRuleIds = effective,
        };
    }
}
