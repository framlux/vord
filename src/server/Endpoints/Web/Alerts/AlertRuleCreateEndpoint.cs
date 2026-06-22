// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>
/// Creates a new custom alert rule for the current tenant.
/// Requires TenantAdmin role and Team subscription.
/// </summary>
public sealed class AlertRuleCreateEndpoint : Endpoint<CreateAlertRuleRequest, ApiResponse<AlertRuleDto>>
{
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IMachineRepository _machineRepo;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAuditLogRepository _auditLog;
    private readonly IDatabaseTransactionProvider _transactionProvider;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertRuleCreateEndpoint"/> class.
    /// </summary>
    public AlertRuleCreateEndpoint(
        IAlertRuleRepository alertRuleRepo,
        IMachineRepository machineRepo,
        ISubscriptionService subscriptionService,
        IAuditLogRepository auditLog,
        IDatabaseTransactionProvider transactionProvider)
    {
        _alertRuleRepo = alertRuleRepo;
        _machineRepo = machineRepo;
        _subscriptionService = subscriptionService;
        _auditLog = auditLog;
        _transactionProvider = transactionProvider;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/alert-rules");
        Policies("TenantAdmin");
        Tags(Services.Billing.EndpointTags.RequiresProSubscription);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CreateAlertRuleRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<AlertRuleDto>.Error("Unauthorized"), ct);

            return;
        }

        // Pro+ gating (null/Free/non-Active → 403) is enforced by ProSubscriptionPreProcessor via
        // the RequiresProSubscription tag. The subscription is still loaded here for the Team-tier
        // check below. The pre-processor guarantees a non-null, Active, non-Free subscription.
        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);

        // Only Team tier can create custom rules
        if ((subscription is null) || (subscription.Tier != SubscriptionTier.Team))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<AlertRuleDto>.Error("Custom alert rules require a Team subscription"), ct);

            return;
        }

        bool canCreate = await _subscriptionService.CanCreateAlertRuleAsync(tenantId.Value, ct);
        if (canCreate == false)
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<AlertRuleDto>.Error("Alert rule limit reached for your subscription tier"), ct);

            return;
        }

        if (Enum.TryParse<AlertMetric>(req.Metric, true, out AlertMetric metric) == false)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<AlertRuleDto>.Error($"Invalid metric value: {req.Metric}"), ct);

            return;
        }

        if (Enum.TryParse<AlertOperator>(req.Operator, true, out AlertOperator op) == false)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<AlertRuleDto>.Error($"Invalid operator value: {req.Operator}"), ct);

            return;
        }

        if (Enum.TryParse<AlertSeverity>(req.Severity, true, out AlertSeverity severity) == false)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<AlertRuleDto>.Error($"Invalid severity value: {req.Severity}"), ct);

            return;
        }

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (userId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<AlertRuleDto>.Error("Unable to identify user"), ct);

            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        AlertRule rule = new()
        {
            TenantId = tenantId.Value,
            Name = req.Name,
            Description = req.Description,
            Metric = metric,
            Operator = op,
            Threshold = req.Threshold,
            DurationMinutes = req.DurationMinutes,
            Severity = severity,
            IsEnabled = true,
            NotifyEmail = req.NotifyEmail,
            NotifyWebhook = req.NotifyWebhook,
            IsCustom = true,
            CreatedByUserId = userId.Value,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Validate machine IDs belong to this tenant before opening the transaction
        List<long> validMachineIds = await _machineRepo.GetActiveMachineIdsForTenantAsync(tenantId.Value, req.MachineIds, ct);
        if (validMachineIds.Count != req.MachineIds.Distinct().Count())
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<AlertRuleDto>.Error("One or more machine IDs are invalid or do not belong to this tenant"), ct);

            return;
        }

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        rule = await _alertRuleRepo.CreateAlertRuleAsync(rule, ct);
        await _alertRuleRepo.SetMachinesForRuleAsync(rule.Id, req.MachineIds, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId.Value, userId.Value, null,
            AuditAction.AlertRuleCreated, AuditResourceType.AlertRule,
            rule.Id.ToString(), rule.Name, null), ct);

        await transaction.CommitAsync(ct);

        AlertRuleDto dto = new()
        {
            Id = rule.Id,
            Name = rule.Name,
            Description = rule.Description,
            Metric = rule.Metric.ToString(),
            Operator = rule.Operator.ToString(),
            Threshold = rule.Threshold,
            DurationMinutes = rule.DurationMinutes,
            Severity = rule.Severity.ToString(),
            IsEnabled = rule.IsEnabled,
            NotifyEmail = rule.NotifyEmail,
            NotifyWebhook = rule.NotifyWebhook,
            IsCustom = rule.IsCustom,
            MachineIds = req.MachineIds,
        };

        await Send.OkAsync(ApiResponse<AlertRuleDto>.Ok(dto, "Alert rule created"), cancellation: ct);
    }
}
