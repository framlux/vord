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

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// Reactivates a canceled subscription by resetting it to the Free tier with Active status.
/// This allows tenants to regain access after full cancellation without going through Stripe checkout.
/// </summary>
public sealed class ReactivateSubscriptionEndpoint : EndpointWithoutRequest<ApiResponse<object>>
{
    private readonly BillingStatus _billingStatus;
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IAuditLogRepository _auditLog;
    private readonly ISubscriptionRepository _subscriptionRepository;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<ReactivateSubscriptionEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="ReactivateSubscriptionEndpoint"/> class.
    /// </summary>
    public ReactivateSubscriptionEndpoint(
        BillingStatus billingStatus,
        IDatabaseTransactionProvider transactionProvider,
        IAuditLogRepository auditLog,
        ISubscriptionRepository subscriptionRepository,
        ISubscriptionService subscriptionService,
        ITenantContext tenantContext,
        ILogger<ReactivateSubscriptionEndpoint> logger)
    {
        _billingStatus = billingStatus;
        _transactionProvider = transactionProvider;
        _auditLog = auditLog;
        _subscriptionRepository = subscriptionRepository;
        _subscriptionService = subscriptionService;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/billing/reactivate");
        Policies(AuthorizationPolicies.TenantAdmin);
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

        if (subscription.Status != SubscriptionStatus.Canceled)
        {
            await HttpContext.SendApiErrorAsync(400, "Subscription is not canceled. No reactivation needed.", ct);

            return;
        }

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        // Reactivate by reverting to Free tier with Active status
        await _subscriptionRepository.UpdateSubscriptionStateAsync(tenantId, SubscriptionTier.Free, SubscriptionStatus.Active, clearCurrentPeriodEnd: true, cancellationToken: ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionUpgraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Account reactivated to Free tier from canceled state", null), ct);

        await transaction.CommitAsync(ct);

        _logger.LogInformation(
            "Subscription reactivated to Free tier for tenant {TenantId}",
            tenantId);

        await Send.OkAsync(new ApiResponse<object>
        {
            Success = true,
            Message = "Your account has been reactivated on the Free tier."
        }, cancellation: ct);
    }
}
