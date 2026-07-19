// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Models.Alerts;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Returns alert rules assigned to a specific machine.
/// </summary>
public sealed class MachineAlertRulesListEndpoint : EndpointWithoutRequest<ApiResponse<List<AlertRuleDto>>>
{
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IMachineRepository _machineRepo;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineAlertRulesListEndpoint"/> class.
    /// </summary>
    public MachineAlertRulesListEndpoint(
        IAlertRuleRepository alertRuleRepo,
        IMachineRepository machineRepo,
        ITenantContext tenantContext)
    {
        _alertRuleRepo = alertRuleRepo;
        _machineRepo = machineRepo;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/{machineId}/alert-rules");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = _tenantContext.TenantId;
        if (tenantId is null)
        {
            await HttpContext.SendApiErrorAsync(401, "Unauthorized", ct);

            return;
        }

        long machineId = Route<long>("machineId");

        Machine? machine = await _machineRepo.GetActiveMachineByIdAsync(machineId, tenantId.Value, ct);
        if (machine is null)
        {
            await HttpContext.SendApiErrorAsync(404, "Machine not found", ct);

            return;
        }

        List<int> ruleIds = await _alertRuleRepo.GetRuleIdsForMachineAsync(machineId, tenantId.Value, ct);

        List<AlertRule> tenantRules = await _alertRuleRepo.GetAlertRulesForTenantAsync(tenantId.Value, ct);

        HashSet<int> assignedRuleIds = new(ruleIds);
        List<AlertRuleDto> dtos = tenantRules
            .Where(r => assignedRuleIds.Contains(r.Id))
            .Select(r => new AlertRuleDto
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                Metric = r.Metric.ToString(),
                Operator = r.Operator.ToString(),
                Threshold = r.Threshold,
                DurationMinutes = r.DurationMinutes,
                Severity = r.Severity.ToString(),
                IsEnabled = r.IsEnabled,
                NotifyEmail = r.NotifyEmail,
                NotifyWebhook = r.NotifyWebhook,
                IsCustom = r.IsCustom,
                MachineIds = [machineId],
                Machines = [new AlertRuleMachineDto { Id = machineId, Name = machine.Name ?? "Unknown" }],
            })
            .ToList();

        await Send.OkAsync(ApiResponse<List<AlertRuleDto>>.Ok(dtos), cancellation: ct);
    }
}
