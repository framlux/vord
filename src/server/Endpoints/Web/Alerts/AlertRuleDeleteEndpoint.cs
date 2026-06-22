// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>
/// Deletes a custom alert rule.
/// Only custom rules can be deleted. Default rules must be disabled instead.
/// </summary>
public sealed class AlertRuleDeleteEndpoint : EndpointWithoutRequest<ApiResponse<bool>>
{
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IAlertEventRepository _alertEventRepo;
    private readonly IAlertConditionStateRepository _alertConditionStateRepo;
    private readonly IAuditLogRepository _auditLog;
    private readonly IDatabaseTransactionProvider _transactionProvider;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertRuleDeleteEndpoint"/> class.
    /// </summary>
    /// <param name="alertRuleRepo">Alert rule repository.</param>
    /// <param name="alertEventRepo">Alert event repository.</param>
    /// <param name="alertConditionStateRepo">Alert condition state repository.</param>
    /// <param name="auditLog">Audit log repository.</param>
    /// <param name="transactionProvider">Provides the cross-repository transaction boundary.</param>
    public AlertRuleDeleteEndpoint(
        IAlertRuleRepository alertRuleRepo,
        IAlertEventRepository alertEventRepo,
        IAlertConditionStateRepository alertConditionStateRepo,
        IAuditLogRepository auditLog,
        IDatabaseTransactionProvider transactionProvider)
    {
        ArgumentNullException.ThrowIfNull(alertRuleRepo);
        ArgumentNullException.ThrowIfNull(alertEventRepo);
        ArgumentNullException.ThrowIfNull(alertConditionStateRepo);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(transactionProvider);

        _alertRuleRepo = alertRuleRepo;
        _alertEventRepo = alertEventRepo;
        _alertConditionStateRepo = alertConditionStateRepo;
        _auditLog = auditLog;
        _transactionProvider = transactionProvider;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Delete("/alert-rules/{id}");
        Policies("TenantAdmin");
        Tags(Services.Billing.EndpointTags.RequiresProSubscription);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<bool>.Error("Unauthorized"), ct);

            return;
        }

        // Pro+ gating is enforced by ProSubscriptionPreProcessor via the RequiresProSubscription tag.
        int ruleId = Route<int>("id");

        AlertRule? rule = await _alertRuleRepo.GetAlertRuleByIdAsync(ruleId, tenantId.Value, ct);

        if (rule is null)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<bool>.Error("Alert rule not found"), ct);

            return;
        }

        if (rule.IsCustom == false)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<bool>.Error("Default rules cannot be deleted. Disable them instead."), ct);

            return;
        }

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);

        // Wrap the multi-table delete in a transaction so a mid-flow failure leaves the rule, its
        // events, and its condition-state rows mutually consistent (all deleted or none).
        using (IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct))
        {
            await _alertEventRepo.ResolveEventsForRuleAsync(ruleId, ct);
            await _alertConditionStateRepo.DeleteForRuleAsync(ruleId, ct);
            await _alertRuleRepo.DeleteAlertRuleAsync(ruleId, tenantId.Value, ct);
            await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
                tenantId.Value, userId, null,
                AuditAction.AlertRuleDeleted, AuditResourceType.AlertRule,
                ruleId.ToString(), rule.Name, null), ct);

            await transaction.CommitAsync(ct);
        }

        await Send.OkAsync(ApiResponse<bool>.Ok(true, "Alert rule deleted"), cancellation: ct);
    }
}
