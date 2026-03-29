// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// Subscription information returned to the UI.
/// </summary>
public sealed class SubscriptionDto
{
    /// <summary>The subscription tier.</summary>
    public string Tier { get; set; } = string.Empty;

    /// <summary>The subscription status.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Maximum machines allowed, null if unlimited.</summary>
    public int? MachineLimit { get; set; }

    /// <summary>Current active machine count.</summary>
    public int MachineCount { get; set; }

    /// <summary>Data retention in days.</summary>
    public int RetentionDays { get; set; }

    /// <summary>End of current billing period.</summary>
    public DateTimeOffset? CurrentPeriodEnd { get; set; }

    /// <summary>Whether the subscription is set to cancel at the end of the billing period.</summary>
    public bool CancelAtPeriodEnd { get; set; }

    /// <summary>The pending action that will occur at period end, if any.</summary>
    public string? PendingAction { get; set; }
}

/// <summary>
/// Returns the current tenant's subscription information.
/// </summary>
public sealed class SubscriptionEndpoint : EndpointWithoutRequest<ApiResponse<SubscriptionDto>>
{
    private readonly ISubscriptionService _subscriptionService;

    /// <summary>
    /// Creates a new instance of the <see cref="SubscriptionEndpoint"/> class.
    /// </summary>
    public SubscriptionEndpoint(ISubscriptionService subscriptionService)
    {
        _subscriptionService = subscriptionService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/billing/subscription");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<SubscriptionDto>.Error("Unauthorized"), ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if (subscription is null)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        int machineCount = await _subscriptionService.GetMachineCountForTenantAsync(tenantId.Value, ct);

        SubscriptionDto dto = new()
        {
            Tier = subscription.Tier.ToString(),
            Status = subscription.Status.ToString(),
            MachineLimit = subscription.MachineLimit,
            MachineCount = machineCount,
            RetentionDays = subscription.RetentionDays,
            CurrentPeriodEnd = subscription.CurrentPeriodEnd,
            CancelAtPeriodEnd = subscription.CancelAtPeriodEnd,
            PendingAction = subscription.PendingAction != PendingSubscriptionAction.None
                ? subscription.PendingAction.ToString()
                : null,
        };

        await Send.OkAsync(ApiResponse<SubscriptionDto>.Ok(dto), cancellation: ct);
    }
}
