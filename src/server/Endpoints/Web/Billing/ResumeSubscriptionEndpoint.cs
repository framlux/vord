// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// Response for resume subscription request.
/// </summary>
public sealed class ResumeSubscriptionResponse
{
    /// <summary>Whether the resume was successful.</summary>
    public bool Success { get; set; }

    /// <summary>Message describing the result.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Resumes a subscription that was set to cancel or downgrade at the end of the billing period.
/// Clears the CancelAtPeriodEnd flag and PendingAction, and tells Stripe to remove cancel_at_period_end.
/// </summary>
public sealed class ResumeSubscriptionEndpoint : EndpointWithoutRequest<ApiResponse<ResumeSubscriptionResponse>>
{
    private readonly IBillingStatus _billingStatus;
    private readonly IDatabaseCache _databaseCache;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IBillingApiClient _billingApiClient;
    private readonly ILogger<ResumeSubscriptionEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="ResumeSubscriptionEndpoint"/> class.
    /// </summary>
    public ResumeSubscriptionEndpoint(
        IBillingStatus billingStatus,
        IDatabaseCache databaseCache,
        ISubscriptionService subscriptionService,
        IBillingApiClient billingApiClient,
        ILogger<ResumeSubscriptionEndpoint> logger)
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
        Post("/billing/resume");
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
                ApiResponse<ResumeSubscriptionResponse>.Error("Unauthorized"), ct);

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
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<ResumeSubscriptionResponse>.Error(
                    "Cannot resume a canceled subscription. Please reactivate your account from the billing page."), ct);

            return;
        }

        if (subscription.CancelAtPeriodEnd == false)
        {
            await Send.OkAsync(ApiResponse<ResumeSubscriptionResponse>.Ok(new ResumeSubscriptionResponse
            {
                Success = true,
                Message = "Subscription is not pending cancellation or downgrade."
            }), cancellation: ct);

            return;
        }

        // Clear the local cancellation/downgrade intent
        await _databaseCache.SetCancelAtPeriodEndAsync(tenantId.Value, false, PendingSubscriptionAction.None, ct);

        // Tell Stripe to remove cancel_at_period_end (best effort)
        Tenant? tenant = await _databaseCache.GetTenantByIdAsync(tenantId.Value, ct);
        if (tenant is not null)
        {
            try
            {
                await _billingApiClient.ResumeSubscriptionAsync(tenant.ExternalId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to resume subscription with Stripe for tenant {TenantId}",
                    tenantId.Value);
            }
        }

        await Send.OkAsync(ApiResponse<ResumeSubscriptionResponse>.Ok(new ResumeSubscriptionResponse
        {
            Success = true,
            Message = "Subscription has been resumed."
        }), cancellation: ct);
    }
}
