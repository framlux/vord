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
/// Request model for updating a webhook endpoint.
/// </summary>
public sealed class UpdateWebhookRequest
{
    /// <summary>Whether the webhook is enabled.</summary>
    public bool IsEnabled { get; set; }
}

/// <summary>
/// Updates an existing webhook endpoint (e.g. toggling enabled state).
/// Requires TenantAdmin role and Pro+ subscription.
/// </summary>
public sealed class WebhookUpdateEndpoint : Endpoint<UpdateWebhookRequest, ApiResponse<WebhookEndpointDto>>
{
    private readonly IWebhookRepository _webhookRepo;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAuditLogRepository _auditLog;

    /// <summary>
    /// Creates a new instance of the <see cref="WebhookUpdateEndpoint"/> class.
    /// </summary>
    public WebhookUpdateEndpoint(IWebhookRepository webhookRepo, ISubscriptionService subscriptionService, IAuditLogRepository auditLog)
    {
        _webhookRepo = webhookRepo;
        _subscriptionService = subscriptionService;
        _auditLog = auditLog;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Put("/webhooks/{id}");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(UpdateWebhookRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<WebhookEndpointDto>.Error("Unauthorized"), ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free) || (subscription.Status != SubscriptionStatus.Active))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<WebhookEndpointDto>.Error("Webhooks require a Pro or Team subscription"), ct);

            return;
        }

        int webhookId = Route<int>("id");

        WebhookEndpoint? webhook = await _webhookRepo.GetWebhookByIdAsync(webhookId, tenantId.Value, ct);

        if (webhook is null)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        await _webhookRepo.UpdateWebhookEnabledAsync(webhookId, req.IsEnabled, ct);

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId.Value, userId, null,
            AuditAction.WebhookUpdated, AuditResourceType.Webhook,
            webhookId.ToString(), webhook.Name, null), ct);

        WebhookEndpointDto dto = new()
        {
            Id = webhook.Id,
            Name = webhook.Name,
            Url = webhook.Url,
            IsEnabled = req.IsEnabled,
            CreatedAt = webhook.CreatedAt,
        };

        await Send.OkAsync(ApiResponse<WebhookEndpointDto>.Ok(dto, "Webhook updated"), cancellation: ct);
    }
}
