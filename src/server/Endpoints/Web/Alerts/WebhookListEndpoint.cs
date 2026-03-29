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
/// Returns webhook endpoints for the current tenant.
/// Requires TenantAdmin role and Pro+ subscription.
/// </summary>
public sealed class WebhookListEndpoint : EndpointWithoutRequest<ApiResponse<List<WebhookEndpointDto>>>
{
    private readonly DatabaseContext _db;
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="WebhookListEndpoint"/> class.
    /// </summary>
    public WebhookListEndpoint(DatabaseContext db, ISubscriptionService subscriptionService)
    {
        _db = db;
        _subscriptionService = subscriptionService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/webhooks");
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
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<List<WebhookEndpointDto>>.Error("Unauthorized"), ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if ((subscription is null) || (subscription.Tier == SubscriptionTier.Free))
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<List<WebhookEndpointDto>>.Error("Webhooks require a Pro or Team subscription"), ct);

            return;
        }

        List<WebhookEndpoint> webhooks = await _db.WebhookEndpoints
            .Where(w => w.TenantId == tenantId.Value)
            .OrderBy(w => w.Name)
            .ToListAsync(ct);

        List<WebhookEndpointDto> dtos = webhooks.Select(w => new WebhookEndpointDto
        {
            Id = w.Id,
            Name = w.Name,
            Url = w.Url,
            IsEnabled = w.IsEnabled,
            CreatedAt = w.CreatedAt,
        }).ToList();

        await Send.OkAsync(ApiResponse<List<WebhookEndpointDto>>.Ok(dtos), cancellation: ct);
    }
}
