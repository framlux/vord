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
    /// Updates the state of a tenant's subscription in a single parameterized UPDATE. Replaces
    /// the previous per-transition mutators (checkout, revert-to-free, past-due, reactivate,
    /// downgrade-to-pro, deactivate, admin update).
    /// </summary>
    /// <param name="tenantId">The tenant whose subscription is updated.</param>
    /// <param name="tier">The new tier, or null to leave the tier unchanged.</param>
    /// <param name="status">The new subscription status.</param>
    /// <param name="clearCurrentPeriodEnd">When true, sets <see cref="TenantSubscription.CurrentPeriodEnd"/> to null; otherwise the column is left unchanged.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows updated (0 when the tenant has no subscription).</returns>
    Task<int> UpdateSubscriptionStateAsync(
        int tenantId,
        SubscriptionTier? tier,
        SubscriptionStatus status,
        bool clearCurrentPeriodEnd = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the current period end of a subscription.
    /// </summary>
    Task UpdateSubscriptionPeriodEndAsync(int tenantId, DateTimeOffset currentPeriodEnd, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the subscription for a tenant.
    /// </summary>
    Task<TenantSubscription?> GetSubscriptionForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the tenant's effective retention days: the per-tenant override retention when present,
    /// otherwise the tier default, falling back to one day when neither is resolvable. Served from the
    /// same short-TTL cache entry as the subscription on the caching decorator, so the telemetry ingest
    /// hot path can stamp a row's retention class without an extra database round-trip.
    /// </summary>
    /// <param name="tenantId">The tenant whose effective retention is resolved.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> GetEffectiveRetentionDaysAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates the cached subscription entry (including its cached effective retention) for a
    /// tenant. Called by paths that change effective retention without routing through this
    /// repository's own mutators — notably per-tenant override edits — so the change takes effect
    /// within one request rather than one cache TTL.
    /// </summary>
    /// <param name="tenantId">The tenant whose cache entry is invalidated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task InvalidateSubscriptionCacheAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all subscriptions where the tier is not Free (i.e., paid subscriptions that have a Stripe counterpart).
    /// </summary>
    Task<List<TenantSubscription>> GetPaidSubscriptionsAsync(CancellationToken cancellationToken = default);

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
