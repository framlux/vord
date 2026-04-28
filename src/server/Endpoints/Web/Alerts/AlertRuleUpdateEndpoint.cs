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
using LinqToDB.Async;

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
    private readonly DatabaseContext _db;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertRuleUpdateEndpoint"/> class.
    /// </summary>
    public AlertRuleUpdateEndpoint(DatabaseContext db, ISubscriptionService subscriptionService)
    {
        _db = db;
        _subscriptionService = subscriptionService;
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
        if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<AlertRuleDto>.Error("Alerting requires a Pro or Team subscription"), ct);

            return;
        }

        int ruleId = Route<int>("id");

        AlertRule? rule = await _db.AlertRules
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.TenantId == tenantId.Value, ct);

        if (rule is null)
        {
            await Send.NotFoundAsync(ct);

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

        await _db.AlertRules
            .Where(r => r.Id == ruleId)
            .Set(r => r.Name, req.Name)
            .Set(r => r.Description, req.Description)
            .Set(r => r.Threshold, req.Threshold)
            .Set(r => r.DurationMinutes, req.DurationMinutes)
            .Set(r => r.Severity, severity)
            .Set(r => r.IsEnabled, req.IsEnabled)
            .Set(r => r.NotifyEmail, req.NotifyEmail)
            .Set(r => r.NotifyWebhook, req.NotifyWebhook)
            .Set(r => r.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(ct);

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
