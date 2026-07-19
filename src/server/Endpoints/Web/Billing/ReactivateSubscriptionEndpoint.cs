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
        IBillingStatus billingStatus,
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
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        if (_billingStatus.IsEnabled == false)
        {
            await HttpContext.SendApiErrorAsync(404, "Billing is not enabled", ct);

            return;
        }

        int? tenantId = _tenantContext.TenantId;
        if (tenantId is null)
        {
            await HttpContext.SendApiErrorAsync(401, "Unauthorized", ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if (subscription is null)
        {
            await HttpContext.SendApiErrorAsync(404, "Subscription not found", ct);

            return;
        }

        if (subscription.Status != SubscriptionStatus.Canceled)
        {
            await HttpContext.SendApiErrorAsync(400, "Subscription is not canceled. No reactivation needed.", ct);

            return;
        }

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        // Reactivate by reverting to Free tier with Active status
        await _subscriptionRepository.RevertSubscriptionToFreeAsync(tenantId.Value, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId.Value, null, null,
            AuditAction.SubscriptionUpgraded, AuditResourceType.Subscription,
            tenantId.Value.ToString(), "Account reactivated to Free tier from canceled state", null), ct);

        await transaction.CommitAsync(ct);

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
