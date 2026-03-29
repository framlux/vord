// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// Response for reactivate subscription request.
/// </summary>
public sealed class ReactivateSubscriptionResponse
{
    /// <summary>Whether the reactivation was successful.</summary>
    public bool Success { get; set; }

    /// <summary>Message describing the result.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Reactivates a canceled subscription by resetting it to the Free tier with Active status.
/// This allows tenants to regain access after full cancellation without going through Stripe checkout.
/// </summary>
public sealed class ReactivateSubscriptionEndpoint : EndpointWithoutRequest<ApiResponse<ReactivateSubscriptionResponse>>
{
    private readonly IBillingStatus _billingStatus;
    private readonly IDatabaseCache _databaseCache;
    private readonly ISubscriptionService _subscriptionService;
    private readonly SubscriptionOptions _subscriptionOptions;
    private readonly ILogger<ReactivateSubscriptionEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="ReactivateSubscriptionEndpoint"/> class.
    /// </summary>
    public ReactivateSubscriptionEndpoint(
        IBillingStatus billingStatus,
        IDatabaseCache databaseCache,
        ISubscriptionService subscriptionService,
        IOptions<SubscriptionOptions> subscriptionOptions,
        ILogger<ReactivateSubscriptionEndpoint> logger)
    {
        _billingStatus = billingStatus;
        _databaseCache = databaseCache;
        _subscriptionService = subscriptionService;
        _subscriptionOptions = subscriptionOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/billing/reactivate");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        if (_billingStatus.IsEnabled == false)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<ReactivateSubscriptionResponse>.Error("Unauthorized"), ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if (subscription is null)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        if (subscription.Status != SubscriptionStatus.Canceled)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<ReactivateSubscriptionResponse>.Error(
                    "Subscription is not canceled. No reactivation needed."), ct);

            return;
        }

        // Reactivate by reverting to Free tier with Active status
        await _databaseCache.RevertSubscriptionToFreeAsync(tenantId.Value, _subscriptionOptions.FreeTierMachineLimit, _subscriptionOptions.FreeTierRetentionDays, ct);

        await _databaseCache.InsertAuditLogAsync(AuditHelper.Create(
            tenantId.Value, null, null,
            AuditAction.SubscriptionUpgraded, AuditResourceType.Subscription,
            tenantId.Value.ToString(), "Account reactivated to Free tier from canceled state", null), ct);

        _logger.LogInformation(
            "Subscription reactivated to Free tier for tenant {TenantId}",
            tenantId.Value);

        await Send.OkAsync(ApiResponse<ReactivateSubscriptionResponse>.Ok(new ReactivateSubscriptionResponse
        {
            Success = true,
            Message = "Your account has been reactivated on the Free tier."
        }), cancellation: ct);
    }
}
