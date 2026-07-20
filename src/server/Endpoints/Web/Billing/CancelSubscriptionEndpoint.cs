// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.Vord.BillingGrpc;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// Cancels the tenant's subscription at the end of the current billing period.
/// Delegates cancellation to the billing-api which manages Stripe state and pending actions.
/// </summary>
public sealed class CancelSubscriptionEndpoint : EndpointWithoutRequest<ApiResponse<BillingActionResponse>>
{
    private readonly BillingStatus _billingStatus;
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IAuditLogRepository _auditLog;
    private readonly ISubscriptionRepository _subscriptionRepository;
    private readonly ITenantRepository _tenantRepository;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ITenantContext _tenantContext;
    private readonly IBillingApiClient _billingApiClient;
    private readonly ILogger<CancelSubscriptionEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="CancelSubscriptionEndpoint"/> class.
    /// </summary>
    public CancelSubscriptionEndpoint(
        BillingStatus billingStatus,
        IDatabaseTransactionProvider transactionProvider,
        IAuditLogRepository auditLog,
        ISubscriptionRepository subscriptionRepository,
        ITenantRepository tenantRepository,
        ISubscriptionService subscriptionService,
        ITenantContext tenantContext,
        IBillingApiClient billingApiClient,
        ILogger<CancelSubscriptionEndpoint> logger)
    {
        _billingStatus = billingStatus;
        _transactionProvider = transactionProvider;
        _auditLog = auditLog;
        _subscriptionRepository = subscriptionRepository;
        _tenantRepository = tenantRepository;
        _subscriptionService = subscriptionService;
        _tenantContext = tenantContext;
        _billingApiClient = billingApiClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/billing/cancel");
        Policies("TenantAdmin");
        Tags(EndpointTags.RequiresTenant);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int tenantId = _tenantContext.RequireTenantId();

        TenantSubscription? subscription = await BillingEndpointGuards.LoadGatedSubscriptionAsync(
            HttpContext, _billingStatus, _subscriptionService, tenantId, ct);
        if (subscription is null)
        {
            return;
        }

        if (subscription.Status == SubscriptionStatus.Canceled)
        {
            await Send.OkAsync(ApiResponse<BillingActionResponse>.Ok(new BillingActionResponse
            {
                Success = true,
                Message = "Subscription is already canceled."
            }), cancellation: ct);

            return;
        }

        // Free tier cancellation takes effect immediately since there is no Stripe subscription
        if (subscription.Tier == SubscriptionTier.Free)
        {
            using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

            await _subscriptionRepository.UpdateSubscriptionStateAsync(tenantId, tier: null, SubscriptionStatus.Canceled, cancellationToken: ct);

            await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
                tenantId, null, null,
                AuditAction.SubscriptionCancelRequested, AuditResourceType.Subscription,
                tenantId.ToString(), "Free tier account canceled immediately", null), ct);

            await transaction.CommitAsync(ct);

            await Send.OkAsync(ApiResponse<BillingActionResponse>.Ok(new BillingActionResponse
            {
                Success = true,
                Message = "Account has been canceled."
            }), cancellation: ct);

            return;
        }

        // For paid tiers, delegate cancellation to the billing-api which manages Stripe state
        Tenant? tenant = await _tenantRepository.GetTenantByIdAsync(tenantId, ct);
        if (tenant is null)
        {
            await HttpContext.SendApiErrorAsync(404, "Tenant not found", ct);

            return;
        }

        // Check if Stripe already reflects a pending cancellation
        StripeSubscriptionStatus stripeStatus = await _billingApiClient.GetSubscriptionStatusAsync(tenant.ExternalId, ct);
        if (stripeStatus.CancelAtPeriodEnd)
        {
            await Send.OkAsync(ApiResponse<BillingActionResponse>.Ok(new BillingActionResponse
            {
                Success = true,
                Message = "Subscription is already set to cancel at the end of the billing period."
            }), cancellation: ct);

            return;
        }

        bool success = await _billingApiClient.CancelSubscriptionAsync(tenant.ExternalId, PendingActionType.CancelAccount, ct);
        if (success == false)
        {
            _logger.LogWarning("Failed to cancel subscription with billing-api for tenant {TenantId}", tenantId);
            await HttpContext.SendApiErrorAsync(502, "Failed to process cancellation. Please try again.", ct);

            return;
        }

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionCancelRequested, AuditResourceType.Subscription,
            tenantId.ToString(), null, null), ct);

        await Send.OkAsync(ApiResponse<BillingActionResponse>.Ok(new BillingActionResponse
        {
            Success = true,
            Message = "Subscription will be canceled at the end of the current billing period."
        }), cancellation: ct);
    }
}
