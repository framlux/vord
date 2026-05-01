// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Security;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>
/// Creates a new webhook endpoint for the current tenant.
/// Requires TenantAdmin role and Pro+ subscription.
/// </summary>
public sealed class WebhookCreateEndpoint : Endpoint<CreateWebhookRequest, ApiResponse<WebhookEndpointDto>>
{
    private readonly IWebhookRepository _webhookRepo;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IWebhookSecretProtector _secretProtector;
    private readonly IAuditLogRepository _auditLog;

    /// <summary>
    /// Creates a new instance of the <see cref="WebhookCreateEndpoint"/> class.
    /// </summary>
    public WebhookCreateEndpoint(IWebhookRepository webhookRepo, ISubscriptionService subscriptionService, IWebhookSecretProtector secretProtector, IAuditLogRepository auditLog)
    {
        _webhookRepo = webhookRepo;
        _subscriptionService = subscriptionService;
        _secretProtector = secretProtector;
        _auditLog = auditLog;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/webhooks");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CreateWebhookRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<WebhookEndpointDto>.Error("Unauthorized"), ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free) || (subscription.Status != SubscriptionStatus.Active))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<WebhookEndpointDto>.Error("Webhooks require a Pro or Team subscription"), ct);

            return;
        }

        bool canCreate = await _subscriptionService.CanCreateWebhookAsync(tenantId.Value, ct);
        if (canCreate == false)
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<WebhookEndpointDto>.Error("Webhook endpoint limit reached for your subscription tier"), ct);

            return;
        }

        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Url))
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<WebhookEndpointDto>.Error("Name and URL are required"), ct);

            return;
        }

        if (req.Name.Length > 250)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<WebhookEndpointDto>.Error("Webhook name must be 250 characters or fewer"), ct);

            return;
        }

        if (req.Url.Length > 2000)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<WebhookEndpointDto>.Error("Webhook URL must be 2000 characters or fewer"), ct);

            return;
        }

        if (SsoOidcEvents.IsUrlSafe(req.Url) == false)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<WebhookEndpointDto>.Error("Webhook URL must be HTTPS and must not point to a private or reserved address"), ct);

            return;
        }

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (userId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<WebhookEndpointDto>.Error("Unable to identify user"), ct);

            return;
        }

        string hexSecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        string encryptedSecret = _secretProtector.Protect(hexSecret);

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId.Value,
            Name = req.Name,
            Url = req.Url,
            Secret = encryptedSecret,
            IsEnabled = true,
            CreatedByUserId = userId.Value,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await _webhookRepo.CreateWebhookAsync(webhook, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId.Value, userId.Value, null,
            AuditAction.WebhookCreated, AuditResourceType.Webhook,
            webhook.Id.ToString(), webhook.Name, null), ct);

        WebhookEndpointDto dto = new()
        {
            Id = webhook.Id,
            Name = webhook.Name,
            Url = webhook.Url,
            IsEnabled = webhook.IsEnabled,
            CreatedAt = webhook.CreatedAt,
            Secret = hexSecret,
        };

        await Send.OkAsync(ApiResponse<WebhookEndpointDto>.Ok(dto, "Webhook created"), cancellation: ct);
    }
}
