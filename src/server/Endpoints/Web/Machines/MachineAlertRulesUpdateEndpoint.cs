// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
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
    private readonly IMachineRepository _machineRepo;
    private readonly IAuditLogRepository _auditLog;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineAlertRulesUpdateEndpoint"/> class.
    /// </summary>
    public MachineAlertRulesUpdateEndpoint(
        IAlertRuleRepository alertRuleRepo,
        IMachineRepository machineRepo,
        IAuditLogRepository auditLog)
    {
        _alertRuleRepo = alertRuleRepo;
        _machineRepo = machineRepo;
        _auditLog = auditLog;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Put("/machines/{machineId}/alert-rules");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(UpdateMachineAlertRulesRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("Unauthorized"), ct);

            return;
        }

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);

        long machineId = Route<long>("machineId");

        Machine? machine = await _machineRepo.GetActiveMachineByIdAsync(machineId, tenantId.Value, ct);
        if (machine is null)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("Machine not found"), ct);

            return;
        }

        if (req.RuleIds.Length > 0)
        {
            List<AlertRule> tenantRules = await _alertRuleRepo.GetAlertRulesForTenantAsync(tenantId.Value, ct);
            List<int> invalidIds = FindInvalidRuleIds(req.RuleIds, tenantRules);

            if (invalidIds.Count > 0)
            {
                HttpContext.Response.StatusCode = 400;
                await HttpContext.Response.WriteAsJsonAsync(
                    ApiResponse<object>.Error("One or more rule IDs are invalid or do not belong to this tenant"), ct);

                return;
            }
        }

        await _alertRuleRepo.SetRulesForMachineAsync(machineId, tenantId.Value, req.RuleIds, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId.Value, userId, machineId,
            AuditAction.MachineAlertRulesUpdated, AuditResourceType.Machine,
            machineId.ToString(), new { RuleIds = req.RuleIds }, null), ct);

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
