// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// Response for cancel subscription request.
/// </summary>
public sealed class CancelSubscriptionResponse
{
    /// <summary>Whether the cancellation intent was recorded.</summary>
    public bool Success { get; set; }

    /// <summary>Message describing the result.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Cancels the tenant's subscription at the end of the current billing period.
/// Records the cancellation intent locally first, then attempts to cancel with Stripe via gRPC.
/// The intent-first approach ensures cancellation is eventually processed even if the gRPC call fails.
/// </summary>
public sealed class CancelSubscriptionEndpoint : EndpointWithoutRequest<ApiResponse<CancelSubscriptionResponse>>
{
    private readonly IBillingStatus _billingStatus;
    private readonly IDatabaseCache _databaseCache;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IBillingApiClient _billingApiClient;
    private readonly ILogger<CancelSubscriptionEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="CancelSubscriptionEndpoint"/> class.
    /// </summary>
    public CancelSubscriptionEndpoint(
        IBillingStatus billingStatus,
        IDatabaseCache databaseCache,
        ISubscriptionService subscriptionService,
        IBillingApiClient billingApiClient,
        ILogger<CancelSubscriptionEndpoint> logger)
    {
        _billingStatus = billingStatus;
        _databaseCache = databaseCache;
        _subscriptionService = subscriptionService;
        _billingApiClient = billingApiClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/billing/cancel");
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
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<CancelSubscriptionResponse>.Error("Unauthorized"), ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if (subscription is null)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        if (subscription.Status == SubscriptionStatus.Canceled)
        {
            await Send.OkAsync(ApiResponse<CancelSubscriptionResponse>.Ok(new CancelSubscriptionResponse
            {
                Success = true,
                Message = "Subscription is already canceled."
            }), cancellation: ct);

            return;
        }

        if (subscription.CancelAtPeriodEnd && (subscription.PendingAction == PendingSubscriptionAction.CancelAccount))
        {
            await Send.OkAsync(ApiResponse<CancelSubscriptionResponse>.Ok(new CancelSubscriptionResponse
            {
                Success = true,
                Message = "Subscription is already set to cancel at the end of the billing period."
            }), cancellation: ct);

            return;
        }

        // Free tier cancellation takes effect immediately since there is no Stripe subscription
        if (subscription.Tier == SubscriptionTier.Free)
        {
            await _databaseCache.DeactivateSubscriptionAsync(tenantId.Value, ct);

            await _databaseCache.InsertAuditLogAsync(AuditHelper.Create(
                tenantId.Value, null, null,
                AuditAction.SubscriptionCancelRequested, AuditResourceType.Subscription,
                tenantId.Value.ToString(), "Free tier account canceled immediately", null), ct);

            await Send.OkAsync(ApiResponse<CancelSubscriptionResponse>.Ok(new CancelSubscriptionResponse
            {
                Success = true,
                Message = "Account has been canceled."
            }), cancellation: ct);

            return;
        }

        // For paid tiers, record the cancellation intent in the local database first
        await _databaseCache.SetCancelAtPeriodEndAsync(tenantId.Value, true, PendingSubscriptionAction.CancelAccount, ct);

        await _databaseCache.InsertAuditLogAsync(AuditHelper.Create(
            tenantId.Value, null, null,
            AuditAction.SubscriptionCancelRequested, AuditResourceType.Subscription,
            tenantId.Value.ToString(), null, null), ct);

        // Attempt to cancel with Stripe via the billing API (best effort)
        Tenant? tenant = await _databaseCache.GetTenantByIdAsync(tenantId.Value, ct);
        if (tenant is not null)
        {
            try
            {
                await _billingApiClient.CancelSubscriptionAsync(tenant.ExternalId, ct);
            }
            catch (Exception ex)
            {
                // The reconciliation service will retry this
                _logger.LogWarning(ex,
                    "Failed to cancel subscription with Stripe for tenant {TenantId}, reconciliation will retry",
                    tenantId.Value);
            }
        }

        await Send.OkAsync(ApiResponse<CancelSubscriptionResponse>.Ok(new CancelSubscriptionResponse
        {
            Success = true,
            Message = "Subscription will be canceled at the end of the current billing period."
        }), cancellation: ct);
    }
}
