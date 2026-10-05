// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using LinqToDB.Linq;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : ISubscriptionRepository
{
    /// <inheritdoc/>
    public async Task<TenantSubscription> CreateTenantSubscriptionAsync(TenantSubscription subscription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        try
        {
            _logger.LogDebug("Creating subscription for tenant {TenantId}", subscription.TenantId);
            int newId = await _db.InsertWithInt32IdentityAsync(subscription, token: cancellationToken);
            subscription.Id = newId;
            _logger.LogInformation("Successfully created subscription {SubscriptionId} for tenant {TenantId}", newId, subscription.TenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create subscription for tenant {TenantId}", subscription.TenantId);
            throw;
        }

        return subscription;
    }

    /// <inheritdoc/>
    public async Task<int> UpdateSubscriptionStateAsync(
        int tenantId,
        SubscriptionTier? tier,
        SubscriptionStatus status,
        bool clearCurrentPeriodEnd = false,
        CancellationToken cancellationToken = default)
    {
        // Enterprise is entered only by applying an agreement, which also records the revision and
        // term end. A plain tier write would leave an Enterprise row with no agreement behind it.
        if (tier == SubscriptionTier.Enterprise)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tier), tier, "Enterprise can only be entered by applying an agreement.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        IUpdatable<TenantSubscription> update = _db.TenantSubscriptions
            // Enterprise is owned by its agreement. Every write here comes from Stripe, a customer
            // action or a Stripe-shaped admin grant, so none may touch it; the filter is in SQL so a
            // stale cached tier or a racing reconciliation pass cannot get around it.
            .Where(s => (s.TenantId == tenantId) && (s.Tier != SubscriptionTier.Enterprise))
            .AsUpdatable()
            .Set(s => s.Status, status)
            .Set(s => s.UpdatedAt, now);

        if (tier is not null)
        {
            update = update.Set(s => s.Tier, tier.Value);
        }

        if (clearCurrentPeriodEnd)
        {
            update = update.Set(s => s.CurrentPeriodEnd, (DateTimeOffset?)null);
        }

        int updated = await update.UpdateAsync(cancellationToken);

        return updated;
    }

    /// <inheritdoc/>
    public async Task<int> UpdateSubscriptionPeriodEndAsync(int tenantId, DateTimeOffset currentPeriodEnd, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int updated = await _db.TenantSubscriptions
            // Enterprise rows are excluded in SQL for the same reason as in UpdateSubscriptionStateAsync.
            .Where(s => (s.TenantId == tenantId) && (s.Tier != SubscriptionTier.Enterprise))
            .Set(s => s.CurrentPeriodEnd, currentPeriodEnd)
            .Set(s => s.UpdatedAt, now)
            .UpdateAsync(cancellationToken);

        return updated;
    }

    /// <inheritdoc/>
    public async Task<TenantSubscription?> GetSubscriptionForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        TenantSubscription? subscription = await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

        return subscription;
    }

    /// <inheritdoc/>
    public async Task<TenantSubscription?> GetSubscriptionForUpdateAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        // The lock and the read are separate statements. Under READ COMMITTED the read starts after the
        // lock is held, so it sees whatever the previous holder committed, which a single locking
        // SELECT would not give the caller's later statements either.
        if (_db.DataProvider.Name.Contains("PostgreSQL"))
        {
            await _db.ExecuteAsync(
                $"SELECT 1 FROM \"{TableNames.TenantSubscriptions}\" WHERE \"TenantId\" = @tenantId FOR UPDATE",
                cancellationToken,
                new DataParameter("@tenantId", tenantId));
        }

        TenantSubscription? subscription = await GetSubscriptionForTenantAsync(tenantId, cancellationToken);

        return subscription;
    }

    /// <inheritdoc/>
    public async Task<int> GetEffectiveRetentionDaysAsync(int tenantId, CancellationToken cancellationToken)
    {
        TenantSubscription? subscription = await GetSubscriptionForTenantAsync(tenantId, cancellationToken);

        if (subscription is null)
        {
            return 1;
        }

        TenantSubscriptionOverride? tenantOverride = await GetOverrideForTenantAsync(tenantId, cancellationToken);
        if (tenantOverride?.RetentionDays is not null)
        {
            return tenantOverride.RetentionDays.Value;
        }

        TierFeatureLimit? tierLimits = await GetLimitsForTierAsync(subscription.Tier, cancellationToken);

        return tierLimits?.RetentionDays ?? 1;
    }

    /// <inheritdoc/>
    public Task InvalidateSubscriptionCacheAsync(int tenantId, CancellationToken cancellationToken)
    {
        // The database-backed repository holds no cache; invalidation is a no-op here and is handled
        // by the caching decorator that wraps it.
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task<List<TenantSubscription>> GetPaidSubscriptionsAsync(CancellationToken cancellationToken)
    {
        // Stripe-billed tiers only. Enterprise is paid but invoiced outside Stripe, so the reconciler
        // that reads this list must never see it.
        List<TenantSubscription> subscriptions = await _db.TenantSubscriptions
            .Where(s => (s.Tier == SubscriptionTier.Pro) || (s.Tier == SubscriptionTier.Team))
            .ToListAsync(cancellationToken);

        return subscriptions;
    }

    /// <inheritdoc/>
    public async Task<List<TenantSubscription>> GetSubscriptionsForTenantsAsync(List<int> tenantIds, CancellationToken cancellationToken)
    {
        List<TenantSubscription> subscriptions = await _db.TenantSubscriptions
            .Where(s => tenantIds.Contains(s.TenantId))
            .ToListAsync(cancellationToken);

        return subscriptions;
    }

    /// <inheritdoc/>
    public async Task<int> SetCancelAtPeriodEndAsync(int tenantId, bool cancelAtPeriodEnd, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        int updated = await _db.TenantSubscriptions
            // Enterprise rows are excluded in SQL for the same reason as in UpdateSubscriptionStateAsync.
            .Where(s => (s.TenantId == tenantId) && (s.Tier != SubscriptionTier.Enterprise))
            .Set(s => s.CancelAtPeriodEnd, cancelAtPeriodEnd)
            .Set(s => s.UpdatedAt, now)
            .UpdateAsync(cancellationToken);

        return updated;
    }

    /// <inheritdoc/>
    public async Task<EnterpriseApplyOutcome> ApplyEnterpriseSubscriptionAsync(
        int tenantId, int revision, DateTimeOffset termEnd, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        TenantSubscription? existing = await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            await _db.InsertWithInt32IdentityAsync(new TenantSubscription
            {
                TenantId = tenantId,
                Tier = SubscriptionTier.Enterprise,
                Status = SubscriptionStatus.Active,
                CurrentPeriodEnd = termEnd,
                CancelAtPeriodEnd = false,
                AppliedAgreementRevision = revision,
                CreatedAt = now,
                UpdatedAt = now,
            }, token: cancellationToken);

            return EnterpriseApplyOutcome.Applied;
        }

        if (existing.AppliedAgreementRevision.HasValue && (existing.AppliedAgreementRevision.Value == revision))
        {
            return EnterpriseApplyOutcome.AlreadyApplied;
        }

        // The revision guard is repeated in SQL so two applies racing past the read above still
        // cannot let the older one land second.
        int updated = await _db.TenantSubscriptions
            .Where(s => (s.TenantId == tenantId) &&
                        ((s.AppliedAgreementRevision == null) || (s.AppliedAgreementRevision < revision)))
            .Set(s => s.Tier, SubscriptionTier.Enterprise)
            .Set(s => s.Status, SubscriptionStatus.Active)
            .Set(s => s.CurrentPeriodEnd, (DateTimeOffset?)termEnd)
            .Set(s => s.CancelAtPeriodEnd, false)
            .Set(s => s.AppliedAgreementRevision, (int?)revision)
            .Set(s => s.UpdatedAt, now)
            .UpdateAsync(cancellationToken);

        if (updated > 0)
        {
            return EnterpriseApplyOutcome.Applied;
        }

        // The guarded update matched nothing, so another apply moved the row after the read above. Whether
        // that was this same revision (a duplicate delivery racing its twin) or a newer one decides what
        // the caller is told: billing-api treats already-applied as success but a stale revision as a
        // failure to investigate.
        int? appliedNow = await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Select(s => s.AppliedAgreementRevision)
            .FirstOrDefaultAsync(cancellationToken);

        return (appliedNow == revision) ? EnterpriseApplyOutcome.AlreadyApplied : EnterpriseApplyOutcome.StaleRevision;
    }
}
