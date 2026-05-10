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
/// Acknowledges an alert event.
/// Requires MachineAdmin role and Pro+ subscription.
/// </summary>
public sealed class AlertEventAcknowledgeEndpoint : EndpointWithoutRequest<ApiResponse<bool>>
{
    private readonly IAlertEventRepository _alertEventRepo;
    private readonly IAuditLogRepository _auditLog;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertEventAcknowledgeEndpoint"/> class.
    /// </summary>
    public AlertEventAcknowledgeEndpoint(
        IAlertEventRepository alertEventRepo,
        IAuditLogRepository auditLog,
        ISubscriptionService subscriptionService)
    {
        _alertEventRepo = alertEventRepo;
        _auditLog = auditLog;
        _subscriptionService = subscriptionService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/alert-events/{id}/acknowledge");
        Policies("MachineAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<bool>.Error("Unauthorized"), ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free) || (subscription.Status != SubscriptionStatus.Active))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<bool>.Error("Alerting requires a Pro or Team subscription"), ct);

            return;
        }

        long eventId = Route<long>("id");

        AlertEvent? alertEvent = await _alertEventRepo.GetAlertEventByIdAsync(eventId, tenantId.Value, ct);

        if (alertEvent is null)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<bool>.Error("Alert event not found"), ct);

            return;
        }

        if (alertEvent.Status != AlertEventStatus.Triggered)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<bool>.Error("Only triggered alerts can be acknowledged"), ct);

            return;
        }

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);

        await _alertEventRepo.AcknowledgeAlertEventAsync(eventId, userId, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId.Value, userId, null,
            AuditAction.AlertEventAcknowledged, AuditResourceType.AlertEvent,
            eventId.ToString(), null, null), ct);

        await Send.OkAsync(ApiResponse<bool>.Ok(true, "Alert acknowledged"), cancellation: ct);
    }
}
