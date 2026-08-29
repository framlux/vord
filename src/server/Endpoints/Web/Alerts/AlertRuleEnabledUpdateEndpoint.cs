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
/// Request model for turning a single alert rule on or off.
/// </summary>
public sealed class UpdateAlertRuleEnabledRequest
{
    /// <summary>Whether the rule should be enabled.</summary>
    public bool IsEnabled { get; set; }
}

/// <summary>
/// Turns a single alert rule on or off.
/// Requires TenantAdmin role and Pro+ subscription.
/// </summary>
/// <remarks>
/// Enabling and disabling a built-in rule is the one change a Pro tenant is entitled to make to it,
/// and it needs a route of its own: the full update endpoint requires every stored field echoed back
/// byte-identically to pass the built-in comparison, and it rewrites the rule's machine assignments
/// unconditionally, so a caller that only wanted to flip a switch would have to resend the whole rule
/// and risk clearing its coverage.
/// </remarks>
public sealed class AlertRuleEnabledUpdateEndpoint : Endpoint<UpdateAlertRuleEnabledRequest, ApiResponse<bool>>
{
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAuditLogRepository _auditLog;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertRuleEnabledUpdateEndpoint"/> class.
    /// </summary>
    public AlertRuleEnabledUpdateEndpoint(
        IAlertRuleRepository alertRuleRepo,
        ISubscriptionService subscriptionService,
        IAuditLogRepository auditLog,
        ITenantContext tenantContext)
    {
        _alertRuleRepo = alertRuleRepo;
        _subscriptionService = subscriptionService;
        _auditLog = auditLog;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Verbs(Http.PATCH);
        Routes("/alert-rules/{id}/enabled");
        Policies(AuthorizationPolicies.TenantAdmin);
        Tags(Services.Billing.EndpointTags.RequiresProSubscription, EndpointTags.RequiresTenant);
        Options(b => b.WithMetadata(new RequiresProFeatureMessage(ProFeatureMessages.Alerting)));
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(UpdateAlertRuleEnabledRequest req, CancellationToken ct)
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

        // Custom rules are Team's in every respect. A Pro tag alone on this endpoint would be a larger
        // hole than the one the update endpoint closes: a Team tenant could downgrade to Pro — which
        // deliberately disables custom rules — and simply toggle every one of them back on, keeping
        // Team-authored rules running on a Pro plan. A downgraded tenant's custom rules stay frozen as
        // they were: assignments intact, rule disabled, not executing.
        if (rule.IsCustom && SubscriptionPolicy.RequiresTeam(subscription))
        {
            await HttpContext.SendApiErrorAsync(403, "Custom rules can only be modified with a Team subscription", ct);

            return;
        }

        bool updated = await _alertRuleRepo.SetAlertRuleEnabledAsync(ruleId, tenantId, req.IsEnabled, ct);

        if (updated == false)
        {
            await HttpContext.SendApiErrorAsync(404, "Alert rule not found", ct);

            return;
        }

        int? userId = _tenantContext.UserId;
        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, userId, null,
            AuditAction.AlertRuleUpdated, AuditResourceType.AlertRule,
            ruleId.ToString(), rule.Name, null), ct);

        await Send.OkAsync(
            ApiResponse<bool>.Ok(req.IsEnabled, req.IsEnabled ? "Alert rule enabled" : "Alert rule disabled"),
            cancellation: ct);
    }
}
