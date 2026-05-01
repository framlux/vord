// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for tenant subscription operations.
/// </summary>
public interface ISubscriptionRepository
{
    /// <summary>
    /// Creates a new tenant subscription in the database.
    /// </summary>
    Task<TenantSubscription> CreateTenantSubscriptionAsync(TenantSubscription subscription, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a subscription after a checkout completes.
    /// </summary>
    Task UpdateSubscriptionOnCheckoutAsync(int tenantId, SubscriptionTier tier, int? alertRuleLimit, int? webhookLimit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the current period end of a subscription.
    /// </summary>
    Task UpdateSubscriptionPeriodEndAsync(int tenantId, DateTimeOffset currentPeriodEnd, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverts a subscription to the Free tier after cancellation.
    /// </summary>
    Task RevertSubscriptionToFreeAsync(int tenantId, int machineLimit, int retentionDays, int alertRuleLimit, int webhookLimit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a subscription status to PastDue after a payment failure.
    /// </summary>
    Task SetSubscriptionPastDueAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the subscription for a tenant.
    /// </summary>
    Task<TenantSubscription?> GetSubscriptionForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets a subscription status to Active after a successful payment recovery.
    /// </summary>
    Task SetSubscriptionActiveAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downgrades a subscription from Team to Pro tier.
    /// </summary>
    Task DowngradeSubscriptionToProAsync(int tenantId, int? alertRuleLimit, int? webhookLimit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the cancel-at-period-end flag and pending action on a subscription.
    /// </summary>
    Task SetCancelAtPeriodEndAsync(int tenantId, bool cancelAtPeriodEnd, PendingSubscriptionAction pendingAction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deactivates a subscription by setting its status to Canceled and clearing pending state.
    /// </summary>
    Task DeactivateSubscriptionAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all subscriptions with pending cancellations that are still active.
    /// </summary>
    Task<List<TenantSubscription>> GetPendingCancellationsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all subscriptions where the tier is not Free (i.e., paid subscriptions that have a Stripe counterpart).
    /// </summary>
    Task<List<TenantSubscription>> GetPaidSubscriptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Provisions a new Free tier subscription for a tenant with the specified limits.
    /// </summary>
    Task<TenantSubscription> InsertSubscriptionAsync(TenantSubscription subscription, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reactivates a Free tier subscription by setting it to Active with updated limits.
    /// </summary>
    Task ReactivateFreeSubscriptionAsync(int subscriptionId, int machineLimit, int retentionDays, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a subscription's tier, status, machine limit, and retention days.
    /// Used by admin interfaces for direct subscription management.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="tier">The new subscription tier.</param>
    /// <param name="status">The new subscription status.</param>
    /// <param name="machineLimit">The new machine limit (null for unlimited).</param>
    /// <param name="retentionDays">The new retention period in days.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows updated.</returns>
    Task<int> UpdateSubscriptionAdminAsync(int tenantId, SubscriptionTier tier, SubscriptionStatus status, int? machineLimit, int retentionDays, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns subscriptions for the given tenant IDs.
    /// </summary>
    /// <param name="tenantIds">The tenant IDs to query.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<TenantSubscription>> GetSubscriptionsForTenantsAsync(List<int> tenantIds, CancellationToken cancellationToken = default);
}
