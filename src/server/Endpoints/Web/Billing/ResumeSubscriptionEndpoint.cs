// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
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
/// Tells the billing-api to remove cancel_at_period_end from Stripe and clear pending actions.
/// </summary>
public sealed class ResumeSubscriptionEndpoint : EndpointWithoutRequest<ApiResponse<ResumeSubscriptionResponse>>
{
    private readonly IBillingStatus _billingStatus;
    private readonly ITenantRepository _tenantRepository;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IBillingApiClient _billingApiClient;
    private readonly ILogger<ResumeSubscriptionEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="ResumeSubscriptionEndpoint"/> class.
    /// </summary>
    public ResumeSubscriptionEndpoint(
        IBillingStatus billingStatus,
        ITenantRepository tenantRepository,
        ISubscriptionService subscriptionService,
        IBillingApiClient billingApiClient,
        ILogger<ResumeSubscriptionEndpoint> logger)
    {
        _billingStatus = billingStatus;
        _tenantRepository = tenantRepository;
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

        Tenant? tenant = await _tenantRepository.GetTenantByIdAsync(tenantId.Value, ct);
        if (tenant is null)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        // Check if the subscription is actually pending cancellation in Stripe
        StripeSubscriptionStatus stripeStatus = await _billingApiClient.GetSubscriptionStatusAsync(tenant.ExternalId, ct);
        if (stripeStatus.CancelAtPeriodEnd == false)
        {
            await Send.OkAsync(ApiResponse<ResumeSubscriptionResponse>.Ok(new ResumeSubscriptionResponse
            {
                Success = true,
                Message = "Subscription is not pending cancellation or downgrade."
            }), cancellation: ct);

            return;
        }

        // Tell billing-api to remove cancel_at_period_end from Stripe and clear pending actions
        bool success = await _billingApiClient.ResumeSubscriptionAsync(tenant.ExternalId, ct);
        if (success == false)
        {
            _logger.LogWarning("Failed to resume subscription with billing-api for tenant {TenantId}", tenantId.Value);
            HttpContext.Response.StatusCode = 502;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<ResumeSubscriptionResponse>.Error("Failed to resume subscription. Please try again."), ct);

            return;
        }

        await Send.OkAsync(ApiResponse<ResumeSubscriptionResponse>.Ok(new ResumeSubscriptionResponse
        {
            Success = true,
            Message = "Subscription has been resumed."
        }), cancellation: ct);
    }
}
