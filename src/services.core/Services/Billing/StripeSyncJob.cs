// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.Vord.BillingGrpc;
using Hangfire;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Hangfire recurring job that periodically synchronizes local subscription state with Stripe.
/// Handles machine quantity sync, tier drift detection, status drift correction, and period end
/// synchronization. Replaces the former StripeSyncService.
/// </summary>
public sealed class StripeSyncJob
{
    private readonly ISubscriptionRepository _subscriptionRepository;
    private readonly ITenantRepository _tenantRepository;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IBillingApiClient _billingApiClient;
    private readonly IBillingWebhookHandler _webhookHandler;
    private readonly BillingOptions _billingOptions;
    private readonly ILogger<StripeSyncJob> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="StripeSyncJob"/> class.
    /// </summary>
    public StripeSyncJob(
        ISubscriptionRepository subscriptionRepository,
        ITenantRepository tenantRepository,
        ISubscriptionService subscriptionService,
        IBillingApiClient billingApiClient,
        IBillingWebhookHandler webhookHandler,
        IOptions<BillingOptions> billingOptions,
        ILogger<StripeSyncJob> logger)
    {
        ArgumentNullException.ThrowIfNull(subscriptionRepository);
        ArgumentNullException.ThrowIfNull(tenantRepository);
        ArgumentNullException.ThrowIfNull(subscriptionService);
        ArgumentNullException.ThrowIfNull(billingApiClient);
        ArgumentNullException.ThrowIfNull(webhookHandler);
        ArgumentNullException.ThrowIfNull(billingOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _subscriptionRepository = subscriptionRepository;
        _tenantRepository = tenantRepository;
        _subscriptionService = subscriptionService;
        _billingApiClient = billingApiClient;
        _webhookHandler = webhookHandler;
        _billingOptions = billingOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs the Stripe sync cycle. Per-tenant errors are swallowed and logged; top-level errors
    /// (e.g., subscription repository failure) propagate so Hangfire records the cycle as failed.
    /// </summary>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    [DisableConcurrentExecution(timeoutInSeconds: 480)]
    [AutomaticRetry(Attempts = 1, DelaysInSeconds = new int[] { 30 })]
    public async Task RunAsync(CancellationToken ct)
    {
        await SyncPaidSubscriptionsAsync(ct);
    }

    internal async Task SyncPaidSubscriptionsAsync(CancellationToken ct)
    {
        List<TenantSubscription> paidSubscriptions = await _subscriptionRepository.GetPaidSubscriptionsAsync(ct);

        if (paidSubscriptions.Count == 0)
        {
            return;
        }

        string proPriceId = _billingOptions.StripeProPriceId;
        string teamPriceId = _billingOptions.StripeTeamPriceId;

        foreach (TenantSubscription subscription in paidSubscriptions)
        {
            try
            {
                Tenant? tenant = await _tenantRepository.GetTenantByIdAsync(subscription.TenantId, ct);
                if (tenant is null)
                {
                    _logger.LogWarning(
                        "Stripe sync: Tenant {TenantId} not found, skipping",
                        subscription.TenantId);

                    continue;
                }

                StripeSubscriptionStatus stripeStatus =
                    await _billingApiClient.GetSubscriptionStatusAsync(tenant.ExternalId, ct);

                // Skip subscriptions that have no Stripe counterpart
                if (stripeStatus.StripeStatus == "none")
                {
                    continue;
                }

                await SyncMachineQuantityAsync(subscription, tenant.ExternalId, stripeStatus, ct);
                await SyncTierAsync(subscription, stripeStatus, proPriceId, teamPriceId, ct);
                await SyncStatusAsync(subscription, stripeStatus, ct);
                await SyncPeriodEndAsync(subscription, stripeStatus, ct);
                await SyncCancelAtPeriodEndAsync(subscription, stripeStatus, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Stripe sync error for tenant {TenantId}",
                    subscription.TenantId);
            }
        }
    }

    internal async Task SyncMachineQuantityAsync(
        TenantSubscription subscription, string tenantExternalId,
        StripeSubscriptionStatus stripeStatus, CancellationToken ct)
    {
        int localMachineCount = await _subscriptionService.GetMachineCountForTenantAsync(subscription.TenantId, ct);

        if (localMachineCount != stripeStatus.Quantity)
        {
            _logger.LogWarning(
                "Stripe sync: Usage drift detected for tenant {TenantId}. Local: {LocalCount}, Stripe: {StripeCount}. Correcting via usage report",
                subscription.TenantId, localMachineCount, stripeStatus.Quantity);

            bool success = await _billingApiClient.ReportMachineUsageAsync(tenantExternalId, localMachineCount, ct);
            if (success == false)
            {
                _logger.LogWarning(
                    "Stripe sync: Failed to report machine usage for tenant {TenantId}",
                    subscription.TenantId);
            }
        }
    }

    internal async Task SyncTierAsync(
        TenantSubscription subscription,
        StripeSubscriptionStatus stripeStatus,
        string proPriceId, string teamPriceId, CancellationToken ct)
    {
        SubscriptionTier? stripeTier = MapBillingTierToSubscriptionTier(stripeStatus.Tier);
        if (stripeTier is null)
        {
            stripeTier = MapPriceIdToTier(stripeStatus.PriceId, proPriceId, teamPriceId);
        }

        if (stripeTier is null)
        {
            return;
        }

        // Safety guard: never downgrade a paid subscription to Free via the sync path. A Free
        // result here means Stripe (or the gRPC cache) returned the wrong tier; the webhook
        // pipeline owns Pro->Free transitions and is the authoritative source for downgrades.
        if (stripeTier.Value == SubscriptionTier.Free)
        {
            _logger.LogWarning(
                "Stripe sync: Stripe reported Free tier for tenant {TenantId} (local tier {LocalTier}); ignoring to avoid silent downgrade",
                subscription.TenantId, subscription.Tier);

            return;
        }

        if (subscription.Tier != stripeTier.Value)
        {
            _logger.LogWarning(
                "Stripe sync: Tier drift detected for tenant {TenantId}. Local: {LocalTier}, Stripe: {StripeTier}. Correcting to match Stripe",
                subscription.TenantId, subscription.Tier, stripeTier.Value);

            await _webhookHandler.HandleTierCorrectionAsync(subscription.TenantId, stripeTier.Value, ct);
        }
    }

    internal async Task SyncStatusAsync(
        TenantSubscription subscription,
        StripeSubscriptionStatus stripeStatus, CancellationToken ct)
    {
        SubscriptionStatus? mappedStatus = MapStripeStatusToLocal(stripeStatus.StripeStatus);
        if (mappedStatus is null)
        {
            return;
        }

        if (subscription.Status != mappedStatus.Value)
        {
            _logger.LogWarning(
                "Stripe sync: Status drift detected for tenant {TenantId}. Local: {LocalStatus}, Stripe: {StripeStatus}. Correcting to match Stripe",
                subscription.TenantId, subscription.Status, stripeStatus.StripeStatus);

            switch (mappedStatus.Value)
            {
                case SubscriptionStatus.Active:
                    await _subscriptionRepository.SetSubscriptionActiveAsync(subscription.TenantId, ct);

                    break;

                case SubscriptionStatus.PastDue:
                    await _subscriptionRepository.SetSubscriptionPastDueAsync(subscription.TenantId, ct);

                    break;

                case SubscriptionStatus.Canceled:
                    await _subscriptionRepository.DeactivateSubscriptionAsync(subscription.TenantId, ct);

                    break;
            }
        }
    }

    internal async Task SyncPeriodEndAsync(
        TenantSubscription subscription,
        StripeSubscriptionStatus stripeStatus, CancellationToken ct)
    {
        if (stripeStatus.CurrentPeriodEnd is null)
        {
            return;
        }

        bool isStale = (subscription.CurrentPeriodEnd is null) ||
            (Math.Abs((subscription.CurrentPeriodEnd.Value - stripeStatus.CurrentPeriodEnd.Value).TotalMinutes) > 1);

        if (isStale)
        {
            _logger.LogWarning(
                "Stripe sync: Period end drift detected for tenant {TenantId}. Local: {LocalPeriodEnd}, Stripe: {StripePeriodEnd}. Updating",
                subscription.TenantId, subscription.CurrentPeriodEnd, stripeStatus.CurrentPeriodEnd.Value);

            await _subscriptionRepository.UpdateSubscriptionPeriodEndAsync(
                subscription.TenantId, stripeStatus.CurrentPeriodEnd.Value, ct);
        }
    }

    internal async Task SyncCancelAtPeriodEndAsync(
        TenantSubscription subscription,
        StripeSubscriptionStatus stripeStatus, CancellationToken ct)
    {
        if (subscription.CancelAtPeriodEnd != stripeStatus.CancelAtPeriodEnd)
        {
            _logger.LogInformation(
                "Stripe sync: CancelAtPeriodEnd drift for tenant {TenantId}. Local: {Local}, Stripe: {Stripe}. Updating",
                subscription.TenantId, subscription.CancelAtPeriodEnd, stripeStatus.CancelAtPeriodEnd);

            await _subscriptionRepository.SetCancelAtPeriodEndAsync(
                subscription.TenantId, stripeStatus.CancelAtPeriodEnd, ct);
        }
    }

    internal SubscriptionTier? MapPriceIdToTier(string priceId, string proPriceId, string teamPriceId)
    {
        if (string.IsNullOrEmpty(priceId))
        {
            return null;
        }

        if (string.Equals(priceId, proPriceId, StringComparison.Ordinal))
        {
            return SubscriptionTier.Pro;
        }

        if (string.Equals(priceId, teamPriceId, StringComparison.Ordinal))
        {
            return SubscriptionTier.Team;
        }

        if ((string.IsNullOrEmpty(_billingOptions.StripeProMonthlyPriceId) == false &&
             string.Equals(priceId, _billingOptions.StripeProMonthlyPriceId, StringComparison.Ordinal)) ||
            (string.IsNullOrEmpty(_billingOptions.StripeProAnnualPriceId) == false &&
             string.Equals(priceId, _billingOptions.StripeProAnnualPriceId, StringComparison.Ordinal)))
        {
            return SubscriptionTier.Pro;
        }

        if ((string.IsNullOrEmpty(_billingOptions.StripeTeamMonthlyPriceId) == false &&
             string.Equals(priceId, _billingOptions.StripeTeamMonthlyPriceId, StringComparison.Ordinal)) ||
            (string.IsNullOrEmpty(_billingOptions.StripeTeamAnnualPriceId) == false &&
             string.Equals(priceId, _billingOptions.StripeTeamAnnualPriceId, StringComparison.Ordinal)))
        {
            return SubscriptionTier.Team;
        }

        return null;
    }

    internal static SubscriptionTier? MapBillingTierToSubscriptionTier(BillingTier tier)
    {
        return tier switch
        {
            BillingTier.Pro => SubscriptionTier.Pro,
            BillingTier.Team => SubscriptionTier.Team,
            BillingTier.Free => SubscriptionTier.Free,
            _ => null,
        };
    }

    internal static SubscriptionStatus? MapStripeStatusToLocal(string stripeStatus)
    {
        return stripeStatus switch
        {
            "active" => SubscriptionStatus.Active,
            "past_due" => SubscriptionStatus.PastDue,
            "unpaid" => SubscriptionStatus.PastDue, // Stripe distinguishes Unpaid from PastDue; we do not.
            "canceled" => SubscriptionStatus.Canceled,
            "trialing" => SubscriptionStatus.Active, // Trial periods are treated as Active locally; the enum has no Trialing value.
            _ => null, // Explicit no-op for incomplete, incomplete_expired, paused, and any unknown status.
        };
    }
}
