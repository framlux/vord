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
    Task UpdateSubscriptionOnCheckoutAsync(int tenantId, SubscriptionTier tier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the current period end of a subscription.
    /// </summary>
    Task UpdateSubscriptionPeriodEndAsync(int tenantId, DateTimeOffset currentPeriodEnd, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverts a subscription to the Free tier after cancellation.
    /// </summary>
    Task RevertSubscriptionToFreeAsync(int tenantId, CancellationToken cancellationToken = default);

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
    Task DowngradeSubscriptionToProAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deactivates a subscription by setting its status to Canceled.
    /// </summary>
    Task DeactivateSubscriptionAsync(int tenantId, CancellationToken cancellationToken = default);

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
    Task ReactivateFreeSubscriptionAsync(int subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a subscription's tier and status.
    /// Used by admin interfaces for direct subscription management.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="tier">The new subscription tier.</param>
    /// <param name="status">The new subscription status.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows updated.</returns>
    Task<int> UpdateSubscriptionAdminAsync(int tenantId, SubscriptionTier tier, SubscriptionStatus status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns subscriptions for the given tenant IDs.
    /// </summary>
    /// <param name="tenantIds">The tenant IDs to query.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<TenantSubscription>> GetSubscriptionsForTenantsAsync(List<int> tenantIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the <see cref="TenantSubscription.CancelAtPeriodEnd"/> flag for a tenant's subscription.
    /// Used by the Stripe sync path to mirror Stripe's cancel-at-period-end state locally so the UI
    /// can reflect a pending cancellation before the subscription transitions to canceled.
    /// </summary>
    /// <param name="tenantId">The tenant whose subscription is being updated.</param>
    /// <param name="cancelAtPeriodEnd">The new cancel-at-period-end value.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task SetCancelAtPeriodEndAsync(int tenantId, bool cancelAtPeriodEnd, CancellationToken cancellationToken = default);
}
