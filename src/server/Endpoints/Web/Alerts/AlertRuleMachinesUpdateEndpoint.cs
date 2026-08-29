// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>
/// Request model for choosing which machines an alert rule watches.
/// </summary>
public sealed class UpdateAlertRuleMachinesRequest
{
    /// <summary>The machine IDs this rule should evaluate against. An empty array means the rule watches nothing.</summary>
    public long[] MachineIds { get; set; } = [];
}

/// <summary>
/// Replaces the set of machines a single alert rule watches.
/// Requires TenantAdmin role and Pro+ subscription.
/// </summary>
/// <remarks>
/// A rule with no machines assigned watches nothing — the evaluation lookup inner-joins the
/// assignment table — and rules are provisioned unassigned, so assignment is the step that makes a
/// built-in rule real. Assignment could already be reached from a machine, one machine at a time,
/// but not from a rule, which is the natural direction for a built-in meant to cover a whole fleet.
/// </remarks>
public sealed class AlertRuleMachinesUpdateEndpoint : Endpoint<UpdateAlertRuleMachinesRequest, ApiResponse<long[]>>
{
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IMachineRepository _machineRepo;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAuditLogRepository _auditLog;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertRuleMachinesUpdateEndpoint"/> class.
    /// </summary>
    public AlertRuleMachinesUpdateEndpoint(
        IAlertRuleRepository alertRuleRepo,
        IMachineRepository machineRepo,
        ISubscriptionService subscriptionService,
        IAuditLogRepository auditLog,
        ITenantContext tenantContext)
    {
        _alertRuleRepo = alertRuleRepo;
        _machineRepo = machineRepo;
        _subscriptionService = subscriptionService;
        _auditLog = auditLog;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Put("/alert-rules/{id}/machines");
        Policies(AuthorizationPolicies.TenantAdmin);
        Tags(Services.Billing.EndpointTags.RequiresProSubscription, EndpointTags.RequiresTenant);
        Options(b => b.WithMetadata(new RequiresProFeatureMessage(ProFeatureMessages.Alerting)));
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(UpdateAlertRuleMachinesRequest req, CancellationToken ct)
    {
        int tenantId = _tenantContext.RequireTenantId();

        // Pro+ gating (null/Free/non-Active → 403) is enforced by ProSubscriptionPreProcessor via the
        // RequiresProSubscription tag. The subscription is still loaded here for the custom-rule
        // Team-tier check below.
        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);

        int ruleId = Route<int>("id");

        AlertRule? rule = await _alertRuleRepo.GetAlertRuleByIdAsync(ruleId, tenantId, ct);

        if (rule is null)
        {
            await HttpContext.SendApiErrorAsync(404, "Alert rule not found", ct);

            return;
        }

        // The entitlement boundary has to be identical for every verb that touches a custom rule. A
        // Pro downgrade freezes those rules exactly as they were — assignments intact, rule disabled —
        // so re-targeting one here would put Team-authored coverage back into service on a Pro plan
        // just as surely as switching it back on would.
        if (rule.IsCustom && SubscriptionPolicy.RequiresTeam(subscription))
        {
            await HttpContext.SendApiErrorAsync(403, "Custom rules can only be modified with a Team subscription", ct);

            return;
        }

        // SetMachinesForRuleAsync silently drops machine ids that do not belong to the tenant, so a
        // request naming another tenant's machine would otherwise report success having assigned
        // fewer machines than asked for. The rejection has to happen here.
        List<long> validMachineIds = await _machineRepo.GetActiveMachineIdsForTenantAsync(tenantId, req.MachineIds, ct);
        if (validMachineIds.Count != req.MachineIds.Distinct().Count())
        {
            await HttpContext.SendApiErrorAsync(400, "One or more machine IDs are invalid or do not belong to this tenant", ct);

            return;
        }

        // Unlike the full update endpoint, an empty array is accepted: it means the rule watches
        // nothing, which is how a rule is parked without turning it off.
        bool assigned = await _alertRuleRepo.SetMachinesForRuleAsync(ruleId, tenantId, req.MachineIds, ct);
        if (assigned == false)
        {
            await HttpContext.SendApiErrorAsync(404, "Alert rule not found", ct);

            return;
        }

        int? userId = _tenantContext.UserId;
        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, userId, null,
            AuditAction.AlertRuleUpdated, AuditResourceType.AlertRule,
            ruleId.ToString(), new { rule.Name, req.MachineIds }, null), ct);

        await Send.OkAsync(
            ApiResponse<long[]>.Ok(req.MachineIds, "Alert rule machines updated"),
            cancellation: ct);
    }
}
