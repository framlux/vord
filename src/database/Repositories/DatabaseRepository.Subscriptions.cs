// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
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
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IUpdatable<TenantSubscription> update = _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
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
    public async Task UpdateSubscriptionPeriodEndAsync(int tenantId, DateTimeOffset currentPeriodEnd, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.CurrentPeriodEnd, currentPeriodEnd)
            .Set(s => s.UpdatedAt, now)
            .UpdateAsync(cancellationToken);
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
    public async Task<List<TenantSubscription>> GetPaidSubscriptionsAsync(CancellationToken cancellationToken)
    {
        List<TenantSubscription> subscriptions = await _db.TenantSubscriptions
            .Where(s => s.Tier != SubscriptionTier.Free)
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
    public async Task SetCancelAtPeriodEndAsync(int tenantId, bool cancelAtPeriodEnd, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.CancelAtPeriodEnd, cancelAtPeriodEnd)
            .Set(s => s.UpdatedAt, now)
            .UpdateAsync(cancellationToken);
    }
}
