// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using System.Security.Cryptography;
using FastEndpoints;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;
using LinqToDB;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Alerts;

/// <summary>
/// Creates a new webhook endpoint for the current tenant.
/// Requires TenantAdmin role and Pro+ subscription.
/// </summary>
public sealed class WebhookCreateEndpoint : Endpoint<CreateWebhookRequest, ApiResponse<WebhookEndpointDto>>
{
    private readonly DatabaseContext _db;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="WebhookCreateEndpoint"/> class.
    /// </summary>
    public WebhookCreateEndpoint(DatabaseContext db, ISubscriptionService subscriptionService)
    {
        _db = db;
        _subscriptionService = subscriptionService;
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
        if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<WebhookEndpointDto>.Error("Webhooks require a Pro or Team subscription"), ct);

            return;
        }

        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Url))
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<WebhookEndpointDto>.Error("Name and URL are required"), ct);

            return;
        }

        if (SsoOidcEvents.IsUrlSafe(req.Url) == false)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<WebhookEndpointDto>.Error("Webhook URL must be HTTPS and must not point to a private or reserved address"), ct);

            return;
        }

        string? userIdStr = User.FindFirstValue(ClaimTypes.Actor);
        int userId = int.TryParse(userIdStr, out int uid) ? uid : 0;

        string secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        WebhookEndpoint webhook = new()
        {
            TenantId = tenantId.Value,
            Name = req.Name,
            Url = req.Url,
            Secret = secret,
            IsEnabled = true,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        webhook.Id = await _db.InsertWithInt32IdentityAsync(webhook, token: ct);

        WebhookEndpointDto dto = new()
        {
            Id = webhook.Id,
            Name = webhook.Name,
            Url = webhook.Url,
            IsEnabled = webhook.IsEnabled,
            CreatedAt = webhook.CreatedAt,
        };

        await Send.OkAsync(ApiResponse<WebhookEndpointDto>.Ok(dto, "Webhook created"), cancellation: ct);
    }
}
