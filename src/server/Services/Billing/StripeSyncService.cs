// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// Background service that periodically synchronizes local subscription state with Stripe.
/// Handles pending cancellation reconciliation, machine quantity sync, tier drift detection,
/// status drift correction, and period end synchronization.
/// </summary>
public sealed class StripeSyncService : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(8);
    private const string LockKey = "lock:stripe-sync";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBillingApiClient _billingApiClient;
    private readonly BillingOptions _billingOptions;
    private readonly IDistributedLock _distributedLock;
    private readonly ILogger<StripeSyncService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="StripeSyncService"/> class.
    /// </summary>
    public StripeSyncService(
        IServiceScopeFactory scopeFactory,
        IBillingApiClient billingApiClient,
        IOptions<BillingOptions> billingOptions,
        IDistributedLock distributedLock,
        ILogger<StripeSyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _billingApiClient = billingApiClient;
        _billingOptions = billingOptions.Value;
        _distributedLock = distributedLock;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await using LockHandle? lockHandle = await _distributedLock.TryAcquireAsync(LockKey, LockTtl);
                if (lockHandle is null)
                {
                    _logger.LogDebug("Stripe sync: another instance holds the lock, skipping this cycle");
                }
                else
                {
                    await RunSyncCycleAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Stripe sync cycle");
            }

            await Task.Delay(SyncInterval, stoppingToken);
        }
    }

    private async Task RunSyncCycleAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        ISubscriptionRepository subscriptionRepository = scope.ServiceProvider.GetRequiredService<ISubscriptionRepository>();
        ITenantRepository tenantRepository = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
        ISubscriptionService subscriptionService = scope.ServiceProvider.GetRequiredService<ISubscriptionService>();

        await ReconcilePendingCancellationsAsync(scope, subscriptionRepository, tenantRepository, ct);
        await SyncPaidSubscriptionsAsync(scope, subscriptionRepository, tenantRepository, subscriptionService, ct);
    }

    private async Task ReconcilePendingCancellationsAsync(
        IServiceScope scope, ISubscriptionRepository subscriptionRepository, ITenantRepository tenantRepository, CancellationToken ct)
    {
        List<TenantSubscription> pendingCancellations = await subscriptionRepository.GetPendingCancellationsAsync(ct);

        if (pendingCancellations.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Reconciling {Count} pending cancellations", pendingCancellations.Count);

        foreach (TenantSubscription subscription in pendingCancellations)
        {
            try
            {
                Tenant? tenant = await tenantRepository.GetTenantByIdAsync(subscription.TenantId, ct);
                if (tenant is null)
                {
                    _logger.LogWarning("Tenant {TenantId} not found during reconciliation", subscription.TenantId);

                    continue;
                }

                StripeSubscriptionStatus stripeStatus =
                    await _billingApiClient.GetSubscriptionStatusAsync(tenant.ExternalId, ct);

                if (stripeStatus.StripeStatus == "canceled" || stripeStatus.StripeStatus == "none")
                {
                    // Subscription already ended in Stripe, process the downgrade
                    Services.Handlers.IBillingWebhookHandler webhookHandler =
                        scope.ServiceProvider.GetRequiredService<Services.Handlers.IBillingWebhookHandler>();
                    await webhookHandler.HandleSubscriptionDeletedAsync(subscription.TenantId, ct);
                    _logger.LogInformation(
                        "Reconciliation: Tenant {TenantId} subscription already canceled in Stripe, processed downgrade",
                        subscription.TenantId);
                }
                else if (stripeStatus.CancelAtPeriodEnd == false)
                {
                    // Stripe doesn't reflect the cancellation yet, retry
                    bool success = await _billingApiClient.CancelSubscriptionAsync(tenant.ExternalId, ct);
                    if (success)
                    {
                        _logger.LogInformation(
                            "Reconciliation: Successfully retried cancellation for tenant {TenantId}",
                            subscription.TenantId);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Reconciliation: Failed to retry cancellation for tenant {TenantId}",
                            subscription.TenantId);
                    }
                }
                else
                {
                    _logger.LogDebug(
                        "Reconciliation: Tenant {TenantId} cancellation already reflected in Stripe",
                        subscription.TenantId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Reconciliation error for tenant {TenantId}",
                    subscription.TenantId);
            }
        }
    }

    private async Task SyncPaidSubscriptionsAsync(
        IServiceScope scope, ISubscriptionRepository subscriptionRepository, ITenantRepository tenantRepository,
        ISubscriptionService subscriptionService, CancellationToken ct)
    {
        List<TenantSubscription> paidSubscriptions = await subscriptionRepository.GetPaidSubscriptionsAsync(ct);

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
                Tenant? tenant = await tenantRepository.GetTenantByIdAsync(subscription.TenantId, ct);
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

                await SyncMachineQuantityAsync(subscription, tenant.ExternalId, stripeStatus, subscriptionService, ct);
                await SyncTierAsync(subscription, stripeStatus, scope, proPriceId, teamPriceId, ct);
                await SyncStatusAsync(subscription, stripeStatus, subscriptionRepository, ct);
                await SyncPeriodEndAsync(subscription, stripeStatus, subscriptionRepository, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Stripe sync error for tenant {TenantId}",
                    subscription.TenantId);
            }
        }
    }

    private async Task SyncMachineQuantityAsync(
        TenantSubscription subscription, string tenantExternalId,
        StripeSubscriptionStatus stripeStatus,
        ISubscriptionService subscriptionService, CancellationToken ct)
    {
        int localMachineCount = await subscriptionService.GetMachineCountForTenantAsync(subscription.TenantId, ct);

        if (localMachineCount != stripeStatus.Quantity)
        {
            bool success = await _billingApiClient.UpdateQuantityAsync(tenantExternalId, localMachineCount, ct);
            if (success)
            {
                _logger.LogWarning(
                    "Stripe sync: Updated machine quantity for tenant {TenantId} from {StripeQuantity} to {LocalQuantity}",
                    subscription.TenantId, stripeStatus.Quantity, localMachineCount);
            }
            else
            {
                _logger.LogWarning(
                    "Stripe sync: Failed to update machine quantity for tenant {TenantId}",
                    subscription.TenantId);
            }
        }
    }

    private async Task SyncTierAsync(
        TenantSubscription subscription,
        StripeSubscriptionStatus stripeStatus,
        IServiceScope scope,
        string proPriceId, string teamPriceId, CancellationToken ct)
    {
        SubscriptionTier? stripeTier = MapPriceIdToTier(stripeStatus.PriceId, proPriceId, teamPriceId);
        if (stripeTier is null)
        {
            // Unknown price ID, cannot determine tier
            return;
        }

        if (subscription.Tier != stripeTier.Value)
        {
            _logger.LogWarning(
                "Stripe sync: Tier drift detected for tenant {TenantId}. Local: {LocalTier}, Stripe: {StripeTier}. Correcting to match Stripe",
                subscription.TenantId, subscription.Tier, stripeTier.Value);

            Services.Handlers.IBillingWebhookHandler webhookHandler =
                scope.ServiceProvider.GetRequiredService<Services.Handlers.IBillingWebhookHandler>();
            await webhookHandler.HandleCheckoutCompletedAsync(subscription.TenantId, stripeTier.Value, ct);
        }
    }

    private async Task SyncStatusAsync(
        TenantSubscription subscription,
        StripeSubscriptionStatus stripeStatus, ISubscriptionRepository subscriptionRepository, CancellationToken ct)
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
                    await subscriptionRepository.SetSubscriptionActiveAsync(subscription.TenantId, ct);

                    break;

                case SubscriptionStatus.PastDue:
                    await subscriptionRepository.SetSubscriptionPastDueAsync(subscription.TenantId, ct);

                    break;

                case SubscriptionStatus.Canceled:
                    await subscriptionRepository.DeactivateSubscriptionAsync(subscription.TenantId, ct);

                    break;
            }
        }
    }

    private async Task SyncPeriodEndAsync(
        TenantSubscription subscription,
        StripeSubscriptionStatus stripeStatus, ISubscriptionRepository subscriptionRepository, CancellationToken ct)
    {
        if (stripeStatus.CurrentPeriodEnd is null)
        {
            return;
        }

        // Consider period end stale if it differs by more than a minute
        bool isStale = (subscription.CurrentPeriodEnd is null) ||
            (Math.Abs((subscription.CurrentPeriodEnd.Value - stripeStatus.CurrentPeriodEnd.Value).TotalMinutes) > 1);

        if (isStale)
        {
            _logger.LogWarning(
                "Stripe sync: Period end drift detected for tenant {TenantId}. Local: {LocalPeriodEnd}, Stripe: {StripePeriodEnd}. Updating",
                subscription.TenantId, subscription.CurrentPeriodEnd, stripeStatus.CurrentPeriodEnd.Value);

            await subscriptionRepository.UpdateSubscriptionPeriodEndAsync(
                subscription.TenantId, stripeStatus.CurrentPeriodEnd.Value, ct);
        }
    }

    private static SubscriptionTier? MapPriceIdToTier(string priceId, string proPriceId, string teamPriceId)
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

        return null;
    }

    private static SubscriptionStatus? MapStripeStatusToLocal(string stripeStatus)
    {
        return stripeStatus switch
        {
            "active" => SubscriptionStatus.Active,
            "past_due" => SubscriptionStatus.PastDue,
            "canceled" => SubscriptionStatus.Canceled,
            // Other Stripe statuses (trialing, incomplete, etc.) are not mapped
            _ => null,
        };
    }
}
