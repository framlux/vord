// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
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
/// Rotates the signing secret for a webhook endpoint and returns the new secret once.
/// Requires TenantAdmin role and Pro+ subscription.
/// </summary>
public sealed class WebhookRotateSecretEndpoint : EndpointWithoutRequest<ApiResponse<WebhookEndpointDto>>
{
    private readonly DatabaseContext _db;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="WebhookRotateSecretEndpoint"/> class.
    /// </summary>
    public WebhookRotateSecretEndpoint(DatabaseContext db, ISubscriptionService subscriptionService)
    {
        _db = db;
        _subscriptionService = subscriptionService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/webhooks/{id}/rotate-secret");
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
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<WebhookEndpointDto>.Error("Unauthorized"), ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<WebhookEndpointDto>.Error("Webhooks require a Pro or Team subscription"), ct);

            return;
        }

        int webhookId = Route<int>("id");

        WebhookEndpoint? webhook = await _db.WebhookEndpoints
            .FirstOrDefaultAsync(w => w.Id == webhookId && w.TenantId == tenantId.Value, ct);

        if (webhook is null)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        string newSecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        await _db.WebhookEndpoints
            .Where(w => w.Id == webhookId)
            .Set(w => w.Secret, newSecret)
            .UpdateAsync(ct);

        WebhookEndpointDto dto = new()
        {
            Id = webhook.Id,
            Name = webhook.Name,
            Url = webhook.Url,
            IsEnabled = webhook.IsEnabled,
            CreatedAt = webhook.CreatedAt,
            Secret = newSecret,
        };

        await Send.OkAsync(ApiResponse<WebhookEndpointDto>.Ok(dto, "Secret rotated"), cancellation: ct);
    }
}
