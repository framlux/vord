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
/// Deletes a webhook endpoint.
/// Requires TenantAdmin role and Pro+ subscription.
/// </summary>
public sealed class WebhookDeleteEndpoint : EndpointWithoutRequest<ApiResponse<bool>>
{
    private readonly IWebhookRepository _webhookRepo;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAuditLogRepository _auditLog;

    /// <summary>
    /// Creates a new instance of the <see cref="WebhookDeleteEndpoint"/> class.
    /// </summary>
    public WebhookDeleteEndpoint(IWebhookRepository webhookRepo, ISubscriptionService subscriptionService, IAuditLogRepository auditLog)
    {
        _webhookRepo = webhookRepo;
        _subscriptionService = subscriptionService;
        _auditLog = auditLog;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Delete("/webhooks/{id}");
        Policies("TenantAdmin");
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
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<bool>.Error("Webhooks require a Pro or Team subscription"), ct);

            return;
        }

        int webhookId = Route<int>("id");

        WebhookEndpoint? webhook = await _webhookRepo.GetWebhookByIdAsync(webhookId, tenantId.Value, ct);

        if (webhook is null)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<bool>.Error("Webhook not found"), ct);

            return;
        }

        await _webhookRepo.DeleteWebhookAsync(webhookId, ct);

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId.Value, userId, null,
            AuditAction.WebhookDeleted, AuditResourceType.Webhook,
            webhookId.ToString(), webhook.Name, null), ct);

        await Send.OkAsync(ApiResponse<bool>.Ok(true, "Webhook deleted"), cancellation: ct);
    }
}
