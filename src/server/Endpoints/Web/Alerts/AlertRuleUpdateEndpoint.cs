// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>
/// Request model for updating an alert rule.
/// </summary>
public sealed class UpdateAlertRuleRequest
{
    /// <summary>The rule name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description.</summary>
    public string? Description { get; set; }

    /// <summary>The threshold value.</summary>
    public decimal Threshold { get; set; }

    /// <summary>Duration in minutes before firing.</summary>
    public int DurationMinutes { get; set; }

    /// <summary>The severity level.</summary>
    public string Severity { get; set; } = string.Empty;

    /// <summary>Whether the rule is enabled.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Whether email notifications are enabled.</summary>
    public bool NotifyEmail { get; set; }

    /// <summary>Whether webhook notifications are enabled.</summary>
    public bool NotifyWebhook { get; set; }
}

/// <summary>
/// Updates an existing alert rule.
/// Requires TenantAdmin role and Pro+ subscription.
/// </summary>
public sealed class AlertRuleUpdateEndpoint : Endpoint<UpdateAlertRuleRequest, ApiResponse<AlertRuleDto>>
{
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAuditLogRepository _auditLog;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertRuleUpdateEndpoint"/> class.
    /// </summary>
    public AlertRuleUpdateEndpoint(IAlertRuleRepository alertRuleRepo, ISubscriptionService subscriptionService, IAuditLogRepository auditLog)
    {
        _alertRuleRepo = alertRuleRepo;
        _subscriptionService = subscriptionService;
        _auditLog = auditLog;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Put("/alert-rules/{id}");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(UpdateAlertRuleRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<AlertRuleDto>.Error("Unauthorized"), ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free) || (subscription.Status != SubscriptionStatus.Active))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<AlertRuleDto>.Error("Alerting requires a Pro or Team subscription"), ct);

            return;
        }

        int ruleId = Route<int>("id");

        AlertRule? rule = await _alertRuleRepo.GetAlertRuleByIdAsync(ruleId, tenantId.Value, ct);

        if (rule is null)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        if (rule.IsCustom && (subscription.Tier != SubscriptionTier.Team))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<AlertRuleDto>.Error("Custom rules can only be modified with a Team subscription"), ct);

            return;
        }

        if (string.IsNullOrWhiteSpace(req.Name))
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<AlertRuleDto>.Error("Rule name is required"), ct);

            return;
        }

        if (req.Name.Length > 250)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<AlertRuleDto>.Error("Rule name must be 250 characters or fewer"), ct);

            return;
        }

        if ((req.Description is not null) && (req.Description.Length > 2000))
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<AlertRuleDto>.Error("Description must be 2000 characters or fewer"), ct);

            return;
        }

        if (req.DurationMinutes < 0)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<AlertRuleDto>.Error("Duration must be zero or positive"), ct);

            return;
        }

        if (Enum.TryParse<AlertSeverity>(req.Severity, true, out AlertSeverity severity) == false)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<AlertRuleDto>.Error("Invalid severity"), ct);

            return;
        }

        // Validate threshold range based on the rule's metric type
        bool isPercentageMetric = rule.Metric is AlertMetric.CpuUsage or AlertMetric.MemoryUsage or AlertMetric.DiskUsage;
        bool isBinaryMetric = rule.Metric is AlertMetric.MachineOffline or AlertMetric.DiskHealth;

        if (isPercentageMetric && ((req.Threshold < 0) || (req.Threshold > 100)))
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<AlertRuleDto>.Error("Threshold for percentage metrics must be between 0 and 100"), ct);

            return;
        }

        if (isBinaryMetric && (req.Threshold != 0) && (req.Threshold != 1))
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<AlertRuleDto>.Error("Threshold for this metric must be 0 or 1"), ct);

            return;
        }

        if ((isPercentageMetric == false) && (isBinaryMetric == false) && (req.Threshold < 0))
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<AlertRuleDto>.Error("Threshold must be zero or positive"), ct);

            return;
        }

        await _alertRuleRepo.UpdateAlertRuleAsync(
            ruleId, tenantId.Value,
            req.Name, req.Description,
            req.Threshold, req.DurationMinutes,
            severity, req.IsEnabled,
            req.NotifyEmail, req.NotifyWebhook, ct);

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId.Value, userId, null,
            AuditAction.AlertRuleUpdated, AuditResourceType.AlertRule,
            ruleId.ToString(), req.Name, null), ct);

        AlertRuleDto dto = new()
        {
            Id = rule.Id,
            Name = req.Name,
            Description = req.Description,
            Metric = rule.Metric.ToString(),
            Operator = rule.Operator.ToString(),
            Threshold = req.Threshold,
            DurationMinutes = req.DurationMinutes,
            Severity = severity.ToString(),
            IsEnabled = req.IsEnabled,
            NotifyEmail = req.NotifyEmail,
            NotifyWebhook = req.NotifyWebhook,
            IsCustom = rule.IsCustom,
        };

        await Send.OkAsync(ApiResponse<AlertRuleDto>.Ok(dto, "Alert rule updated"), cancellation: ct);
    }
}
