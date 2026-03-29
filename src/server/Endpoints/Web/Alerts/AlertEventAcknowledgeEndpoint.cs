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
/// Acknowledges an alert event.
/// Requires MachineAdmin role and Pro+ subscription.
/// </summary>
public sealed class AlertEventAcknowledgeEndpoint : EndpointWithoutRequest<ApiResponse<bool>>
{
    private readonly DatabaseContext _db;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="AlertEventAcknowledgeEndpoint"/> class.
    /// </summary>
    public AlertEventAcknowledgeEndpoint(DatabaseContext db, ISubscriptionService subscriptionService)
    {
        _db = db;
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
        if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<bool>.Error("Alerting requires a Pro or Team subscription"), ct);

            return;
        }

        long eventId = Route<long>("id");

        AlertEvent? alertEvent = await _db.AlertEvents
            .FirstOrDefaultAsync(e => e.Id == eventId && e.TenantId == tenantId.Value, ct);

        if (alertEvent is null)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        if (alertEvent.Status != AlertEventStatus.Triggered)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<bool>.Error("Only triggered alerts can be acknowledged"), ct);

            return;
        }

        await _db.AlertEvents
            .Where(e => e.Id == eventId)
            .Set(e => e.Status, AlertEventStatus.Acknowledged)
            .Set(e => e.AcknowledgedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(ct);

        await Send.OkAsync(ApiResponse<bool>.Ok(true, "Alert acknowledged"), cancellation: ct);
    }
}
