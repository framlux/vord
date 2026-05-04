// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// Request for downgrading a subscription.
/// </summary>
public sealed class DowngradeSubscriptionRequest
{
    /// <summary>The target tier to downgrade to ("free" or "pro").</summary>
    public string TargetTier { get; set; } = string.Empty;
}

/// <summary>
/// Response for downgrade subscription request.
/// </summary>
public sealed class DowngradeSubscriptionResponse
{
    /// <summary>Whether the downgrade was initiated successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Message describing the result.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Downgrades the tenant's subscription to a lower tier.
/// Team to Pro is an immediate price swap. Any downgrade to Free takes effect at period end.
/// </summary>
public sealed class DowngradeSubscriptionEndpoint : Endpoint<DowngradeSubscriptionRequest, ApiResponse<DowngradeSubscriptionResponse>>
{
    private readonly IBillingStatus _billingStatus;
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IAuditLogRepository _auditLog;
    private readonly ISubscriptionRepository _subscriptionRepository;
    private readonly ITenantRepository _tenantRepository;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IBillingApiClient _billingApiClient;
    private readonly IDowngradeGuardService _downgradeGuardService;
    private readonly IDowngradeCleanupService _downgradeCleanupService;
    private readonly ITierFeatureLimitRepository _tierLimitRepo;
    private readonly ILogger<DowngradeSubscriptionEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="DowngradeSubscriptionEndpoint"/> class.
    /// </summary>
    public DowngradeSubscriptionEndpoint(
        IBillingStatus billingStatus,
        IDatabaseTransactionProvider transactionProvider,
        IAuditLogRepository auditLog,
        ISubscriptionRepository subscriptionRepository,
        ITenantRepository tenantRepository,
        ISubscriptionService subscriptionService,
        IBillingApiClient billingApiClient,
        IDowngradeGuardService downgradeGuardService,
        IDowngradeCleanupService downgradeCleanupService,
        ITierFeatureLimitRepository tierLimitRepo,
        ILogger<DowngradeSubscriptionEndpoint> logger)
    {
        _billingStatus = billingStatus;
        _transactionProvider = transactionProvider;
        _auditLog = auditLog;
        _subscriptionRepository = subscriptionRepository;
        _tenantRepository = tenantRepository;
        _subscriptionService = subscriptionService;
        _billingApiClient = billingApiClient;
        _downgradeGuardService = downgradeGuardService;
        _downgradeCleanupService = downgradeCleanupService;
        _tierLimitRepo = tierLimitRepo;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/billing/downgrade");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(DowngradeSubscriptionRequest req, CancellationToken ct)
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
                ApiResponse<DowngradeSubscriptionResponse>.Error("Unauthorized"), ct);

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
            await SendErrorResponse(400, "Cannot downgrade a canceled subscription.", ct);

            return;
        }

        // Parse and validate the target tier
        string targetTierStr = req.TargetTier?.ToLowerInvariant() ?? string.Empty;
        if ((string.Equals(targetTierStr, "free", StringComparison.Ordinal) == false) &&
            (string.Equals(targetTierStr, "pro", StringComparison.Ordinal) == false))
        {
            await SendErrorResponse(400, "Target tier must be 'free' or 'pro'.", ct);

            return;
        }

        // Validate the downgrade path
        SubscriptionTier currentTier = subscription.Tier;
        bool isDowngradeToFree = string.Equals(targetTierStr, "free", StringComparison.Ordinal);
        bool isDowngradeToPro = string.Equals(targetTierStr, "pro", StringComparison.Ordinal);

        if (currentTier == SubscriptionTier.Free)
        {
            await SendErrorResponse(400, "Already on the Free tier. Cannot downgrade further.", ct);

            return;
        }

        if ((currentTier == SubscriptionTier.Pro) && isDowngradeToPro)
        {
            await SendErrorResponse(400, "Already on the Pro tier.", ct);

            return;
        }

        // OIDC lockout guard for downgrades from Team
        if (currentTier == SubscriptionTier.Team)
        {
            bool canDowngrade = await _downgradeGuardService.CanDowngradeFromTeamAsync(tenantId.Value, ct);
            if (canDowngrade == false)
            {
                await SendErrorResponse(400,
                    "Cannot downgrade: at least one Tenant Admin must log in with a social provider (GitHub, Google, or Microsoft) before downgrading from Team tier.",
                    ct);

                return;
            }
        }

        // Team -> Pro: immediate price swap
        if ((currentTier == SubscriptionTier.Team) && isDowngradeToPro)
        {
            await HandleTeamToProDowngradeAsync(tenantId.Value, ct);

            return;
        }

        // Any -> Free: cancel at period end with DowngradeToFree pending action
        if (isDowngradeToFree)
        {
            await HandleDowngradeToFreeAsync(tenantId.Value, ct);

            return;
        }

        await SendErrorResponse(400, "Invalid downgrade path.", ct);
    }

    private async Task HandleTeamToProDowngradeAsync(int tenantId, CancellationToken ct)
    {
        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        // Proactively update local subscription to avoid stale data before webhook arrives
        await _subscriptionRepository.DowngradeSubscriptionToProAsync(tenantId, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionDowngradeRequested, AuditResourceType.Subscription,
            tenantId.ToString(), "Immediate downgrade from Team to Pro", null), ct);

        await transaction.CommitAsync(ct);

        // Clean up Team-only resources after the transaction commits
        await _downgradeCleanupService.CleanupForProTierAsync(tenantId, ct);

        // Swap the Stripe price (best effort, webhook will also fire)
        Tenant? tenant = await _tenantRepository.GetTenantByIdAsync(tenantId, ct);
        if (tenant is not null)
        {
            try
            {
                await _billingApiClient.SwapSubscriptionPriceAsync(tenant.ExternalId, "pro", ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to swap Stripe price for tenant {TenantId} during Team->Pro downgrade",
                    tenantId);
            }
        }

        await Send.OkAsync(ApiResponse<DowngradeSubscriptionResponse>.Ok(new DowngradeSubscriptionResponse
        {
            Success = true,
            Message = "Subscription has been downgraded to Pro."
        }), cancellation: ct);
    }

    private async Task HandleDowngradeToFreeAsync(int tenantId, CancellationToken ct)
    {
        // Check that current machine count does not exceed Free tier limit
        int machineCount = await _subscriptionService.GetMachineCountForTenantAsync(tenantId, ct);
        TierFeatureLimit? freeLimits = await _tierLimitRepo.GetLimitsForTierAsync(SubscriptionTier.Free, ct);
        int freeTierLimit = freeLimits?.MachineLimit ?? 3;
        if (machineCount > freeTierLimit)
        {
            await SendErrorResponse(400,
                $"Cannot downgrade to Free: you have {machineCount} active machines but the Free tier allows {freeTierLimit}. Please remove machines before downgrading.",
                ct);

            return;
        }

        // Delegate cancellation to the billing-api which manages Stripe state and pending actions
        Tenant? tenant = await _tenantRepository.GetTenantByIdAsync(tenantId, ct);
        if (tenant is null)
        {
            await SendErrorResponse(404, "Tenant not found.", ct);

            return;
        }

        bool success = await _billingApiClient.CancelSubscriptionAsync(tenant.ExternalId, PendingActions.DowngradeToFree, ct);
        if (success == false)
        {
            _logger.LogWarning("Failed to cancel Stripe subscription for tenant {TenantId} during downgrade to Free", tenantId);
            await SendErrorResponse(502, "Failed to process downgrade. Please try again.", ct);

            return;
        }

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionDowngradeRequested, AuditResourceType.Subscription,
            tenantId.ToString(), "Downgrade to Free at period end", null), ct);

        await Send.OkAsync(ApiResponse<DowngradeSubscriptionResponse>.Ok(new DowngradeSubscriptionResponse
        {
            Success = true,
            Message = "Subscription will be downgraded to Free at the end of the current billing period."
        }), cancellation: ct);
    }

    private async Task SendErrorResponse(int statusCode, string message, CancellationToken ct)
    {
        HttpContext.Response.StatusCode = statusCode;
        await HttpContext.Response.WriteAsJsonAsync(
            ApiResponse<DowngradeSubscriptionResponse>.Error(message), ct);
    }
}
