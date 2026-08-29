// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Request model for updating alert rules assigned to a machine.
/// </summary>
public sealed class UpdateMachineAlertRulesRequest
{
    /// <summary>The alert rule IDs to assign to this machine.</summary>
    public int[] RuleIds { get; set; } = [];
}

/// <summary>
/// Updates the alert rules assigned to a specific machine.
/// Requires TenantAdmin role.
/// </summary>
public sealed class MachineAlertRulesUpdateEndpoint : Endpoint<UpdateMachineAlertRulesRequest, ApiResponse<object>>
{
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IAlertRuleAssignmentService _assignmentService;
    private readonly IAuditLogRepository _auditLog;
    private readonly IMachineRepository _machineRepo;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineAlertRulesUpdateEndpoint"/> class.
    /// </summary>
    public MachineAlertRulesUpdateEndpoint(
        IAlertRuleRepository alertRuleRepo,
        IAlertRuleAssignmentService assignmentService,
        IMachineRepository machineRepo,
        ISubscriptionService subscriptionService,
        IAuditLogRepository auditLog,
        ITenantContext tenantContext)
    {
        _alertRuleRepo = alertRuleRepo;
        _assignmentService = assignmentService;
        _machineRepo = machineRepo;
        _subscriptionService = subscriptionService;
        _auditLog = auditLog;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Put("/machines/{machineId}/alert-rules");
        Policies(AuthorizationPolicies.TenantAdmin);

        // Assignment decides what a rule actually watches, so it is as much a paid operation as
        // authoring one. A Free tenant may read the built-in rules it holds as an upgrade prompt;
        // the matching list endpoint stays open for exactly that reason.
        Tags(Services.Billing.EndpointTags.RequiresProSubscription, EndpointTags.RequiresTenant);
        Options(b => b.WithMetadata(new RequiresProFeatureMessage(ProFeatureMessages.Alerting)));
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(UpdateMachineAlertRulesRequest req, CancellationToken ct)
    {
        int tenantId = _tenantContext.RequireTenantId();

        int? userId = _tenantContext.UserId;

        long machineId = Route<long>("machineId");

        Machine? machine = await _machineRepo.GetActiveMachineByIdAsync(machineId, tenantId, ct);
        if (machine is null)
        {
            await HttpContext.SendApiErrorAsync(404, "Machine not found", ct);

            return;
        }

        List<AlertRule> tenantRules = await _alertRuleRepo.GetAlertRulesForTenantAsync(tenantId, ct);

        if (req.RuleIds.Length > 0)
        {
            List<int> invalidIds = FindInvalidRuleIds(req.RuleIds, tenantRules);

            if (invalidIds.Count > 0)
            {
                await HttpContext.SendApiErrorAsync(400, "One or more rule IDs are invalid or do not belong to this tenant", ct);

                return;
            }
        }

        // Pro+ gating is enforced by ProSubscriptionPreProcessor via the RequiresProSubscription tag.
        // The subscription is loaded here because this write is the same write the rule-side route
        // performs, and it answers to the same custom-rule boundary — which the assignment service
        // owns so that neither direction can drift from the other.
        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);

        MachineRuleAssignmentResult result = await _assignmentService.SetRulesForMachineAsync(
            machineId, tenantId, tenantRules, req.RuleIds, subscription, ct);

        if (result.Outcome == AlertRuleAssignmentOutcome.CustomRuleRequiresTeam)
        {
            await HttpContext.SendApiErrorAsync(403, "Custom rules can only be modified with a Team subscription", ct);

            return;
        }

        if (result.Outcome != AlertRuleAssignmentOutcome.Applied)
        {
            await HttpContext.SendApiErrorAsync(404, "Machine not found", ct);

            return;
        }

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, userId, machineId,
            AuditAction.MachineAlertRulesUpdated, AuditResourceType.Machine,
            machineId.ToString(), new { RuleIds = result.AppliedRuleIds }, null), ct);

        await Send.OkAsync(ApiResponse<object>.Ok(new { }, "Machine alert rules updated"), cancellation: ct);
    }

    /// <summary>
    /// Returns the subset of <paramref name="requestedRuleIds"/> that do not correspond to a rule
    /// owned by the tenant (i.e. are not present in <paramref name="tenantRules"/>). This is the
    /// cross-tenant isolation guard: a non-empty result means the caller tried to assign a rule
    /// that belongs to another tenant or does not exist, and the update must be rejected.
    /// Extracted as an <c>internal static</c> method so the guard can be unit-tested directly.
    /// </summary>
    /// <param name="requestedRuleIds">The rule IDs the caller wants to assign.</param>
    /// <param name="tenantRules">The alert rules owned by the caller's tenant.</param>
    /// <returns>The requested rule IDs that are invalid for this tenant.</returns>
    internal static List<int> FindInvalidRuleIds(IEnumerable<int> requestedRuleIds, IEnumerable<AlertRule> tenantRules)
    {
        ArgumentNullException.ThrowIfNull(requestedRuleIds);
        ArgumentNullException.ThrowIfNull(tenantRules);

        HashSet<int> validRuleIds = new(tenantRules.Select(r => r.Id));

        return requestedRuleIds.Where(id => validRuleIds.Contains(id) == false).ToList();
    }
}
