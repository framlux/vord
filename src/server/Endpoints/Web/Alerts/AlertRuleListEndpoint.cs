// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>
/// Returns alert rules for the current tenant.
/// Requires Pro+ subscription and ViewOnly role.
/// </summary>
public sealed class AlertRuleListEndpoint : EndpointWithoutRequest<ApiResponse<List<AlertRuleDto>>>
{
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IMachineRepository _machineRepo;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertRuleListEndpoint"/> class.
    /// </summary>
    public AlertRuleListEndpoint(
        IAlertRuleRepository alertRuleRepo,
        IMachineRepository machineRepo,
        ISubscriptionService subscriptionService)
    {
        _alertRuleRepo = alertRuleRepo;
        _machineRepo = machineRepo;
        _subscriptionService = subscriptionService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/alert-rules");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<List<AlertRuleDto>>.Error("Unauthorized"), ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free) || (subscription.Status != SubscriptionStatus.Active))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<List<AlertRuleDto>>.Error("Alerting requires a Pro or Team subscription"), ct);

            return;
        }

        List<AlertRule> rules = await _alertRuleRepo.GetAlertRulesForTenantAsync(tenantId.Value, ct);

        // Fetch machine assignments for all rules in one query
        List<int> ruleIds = rules.Select(r => r.Id).ToList();
        Dictionary<int, List<long>> machinesByRule = await _alertRuleRepo.GetMachineIdsForRulesAsync(ruleIds, ct);

        // Fetch machine names for display
        List<long> allMachineIds = machinesByRule.Values.SelectMany(ids => ids).Distinct().ToList();
        Dictionary<long, string> machineNames = await _machineRepo.GetMachineNamesAsync(allMachineIds, ct);

        List<AlertRuleDto> dtos = rules.Select(r =>
        {
            List<long> assignedIds = machinesByRule.GetValueOrDefault(r.Id, []);

            return new AlertRuleDto
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
                MachineIds = assignedIds.ToArray(),
                Machines = assignedIds.Select(mid => new AlertRuleMachineDto
                {
                    Id = mid,
                    Name = machineNames.GetValueOrDefault(mid, "Unknown"),
                }).ToList(),
            };
        }).ToList();

        await Send.OkAsync(ApiResponse<List<AlertRuleDto>>.Ok(dtos), cancellation: ct);
    }
}
