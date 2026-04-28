// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;
using LinqToDB;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>
/// Creates a new custom alert rule for the current tenant.
/// Requires TenantAdmin role and Team subscription.
/// </summary>
public sealed class AlertRuleCreateEndpoint : Endpoint<CreateAlertRuleRequest, ApiResponse<AlertRuleDto>>
{
    private readonly DatabaseContext _db;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertRuleCreateEndpoint"/> class.
    /// </summary>
    public AlertRuleCreateEndpoint(DatabaseContext db, ISubscriptionService subscriptionService)
    {
        _db = db;
        _subscriptionService = subscriptionService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/alert-rules");
        Policies("TenantAdmin");
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

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<AlertRuleDto>.Error("Alerting requires a Pro or Team subscription"), ct);

            return;
        }

        // Only Team tier can create custom rules
        if (subscription.Tier != SubscriptionTier.Team)
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<AlertRuleDto>.Error("Custom alert rules require a Team subscription"), ct);

            return;
        }

        if (string.IsNullOrWhiteSpace(req.Name))
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<AlertRuleDto>.Error("Rule name is required"), ct);

            return;
        }

        if (req.DurationMinutes < 0)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<AlertRuleDto>.Error("Duration must be zero or positive"), ct);

            return;
        }

        if (Enum.TryParse<AlertMetric>(req.Metric, true, out AlertMetric metric) == false)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<AlertRuleDto>.Error("Invalid metric"), ct);

            return;
        }

        if (Enum.TryParse<AlertOperator>(req.Operator, true, out AlertOperator op) == false)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<AlertRuleDto>.Error("Invalid operator"), ct);

            return;
        }

        if (Enum.TryParse<AlertSeverity>(req.Severity, true, out AlertSeverity severity) == false)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<AlertRuleDto>.Error("Invalid severity"), ct);

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

        rule.Id = await _db.InsertWithInt32IdentityAsync(rule, token: ct);

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
        };

        await Send.OkAsync(ApiResponse<AlertRuleDto>.Ok(dto, "Alert rule created"), cancellation: ct);
    }
}
