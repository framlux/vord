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
    /// downgrade-to-pro, deactivate, admin update). Never updates an Enterprise row: that tier is
    /// owned by its agreement, not by Stripe or by a customer action.
    /// </summary>
    /// <param name="tenantId">The tenant whose subscription is updated.</param>
    /// <param name="tier">The new tier, or null to leave the tier unchanged. Must not be Enterprise: that tier is entered only through <see cref="ApplyEnterpriseSubscriptionAsync"/>.</param>
    /// <param name="status">The new subscription status.</param>
    /// <param name="clearCurrentPeriodEnd">When true, sets <see cref="TenantSubscription.CurrentPeriodEnd"/> to null; otherwise the column is left unchanged.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows updated; 0 when the tenant has no subscription or is on Enterprise.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="tier"/> is Enterprise.</exception>
    Task<int> UpdateSubscriptionStateAsync(
        int tenantId,
        SubscriptionTier? tier,
        SubscriptionStatus status,
        bool clearCurrentPeriodEnd = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the current period end of a subscription. Never updates an Enterprise row, whose
    /// period end is the agreement term.
    /// </summary>
    /// <param name="tenantId">The tenant whose subscription is updated.</param>
    /// <param name="currentPeriodEnd">The new current period end.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows updated; 0 when the tenant has no subscription or is on Enterprise.</returns>
    Task<int> UpdateSubscriptionPeriodEndAsync(int tenantId, DateTimeOffset currentPeriodEnd, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the subscription for a tenant.
    /// </summary>
    Task<TenantSubscription?> GetSubscriptionForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Locks the tenant's subscription row until the surrounding transaction ends, then reads it from
    /// the database. A caching decorator never serves or fills this read. Anything that decides what to
    /// do from the tier it reads, and then writes on that decision, calls this first inside its
    /// transaction: a writer that arrives meanwhile (an agreement being applied, a Stripe webhook) waits
    /// for the lock, so what was read is still true when the transaction commits. The lock is a
    /// <c>FOR UPDATE</c> row lock on PostgreSQL and a no-op on SQLite, which has one writer at a time.
    /// Must run inside the caller's transaction.
    /// </summary>
    /// <param name="tenantId">The tenant whose subscription row is locked and read.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The subscription as last committed (or as this transaction has changed it), or null when the tenant has none.</returns>
    Task<TenantSubscription?> GetSubscriptionForUpdateAsync(int tenantId, CancellationToken cancellationToken = default);

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
    /// Gets the subscriptions billed through Stripe: Pro and Team only. Enterprise is paid but invoiced
    /// outside Stripe, so it has no Stripe counterpart and is excluded.
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
    /// can reflect a pending cancellation before the subscription transitions to canceled. Never
    /// updates an Enterprise row.
    /// </summary>
    /// <param name="tenantId">The tenant whose subscription is being updated.</param>
    /// <param name="cancelAtPeriodEnd">The new cancel-at-period-end value.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows updated; 0 when the tenant has no subscription or is on Enterprise.</returns>
    Task<int> SetCancelAtPeriodEndAsync(int tenantId, bool cancelAtPeriodEnd, CancellationToken cancellationToken = default);

    /// <summary>
    /// Puts a tenant on the Enterprise tier for an applied agreement revision: creates the
    /// subscription row if missing, otherwise sets tier Enterprise, status Active, the term end and
    /// the revision. The only write that may move a tenant into Enterprise. Must run inside the
    /// caller's transaction.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="revision">The agreement revision being applied; must be positive.</param>
    /// <param name="termEnd">The end of the agreement term, stored as the current period end.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the revision was applied, already applied, or older than the one applied.</returns>
    Task<EnterpriseApplyOutcome> ApplyEnterpriseSubscriptionAsync(
        int tenantId, int revision, DateTimeOffset termEnd, CancellationToken cancellationToken = default);
}
