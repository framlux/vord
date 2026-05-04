// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
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
    public async Task UpdateSubscriptionOnCheckoutAsync(int tenantId, SubscriptionTier tier, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.Tier, tier)
            .Set(s => s.Status, SubscriptionStatus.Active)
            .Set(s => s.UpdatedAt, now)
            .UpdateAsync(cancellationToken);
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
    public async Task RevertSubscriptionToFreeAsync(int tenantId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.Tier, SubscriptionTier.Free)
            .Set(s => s.Status, SubscriptionStatus.Active)
            .Set(s => s.UpdatedAt, now)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task SetSubscriptionPastDueAsync(int tenantId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.Status, SubscriptionStatus.PastDue)
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
    public async Task SetSubscriptionActiveAsync(int tenantId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.Status, SubscriptionStatus.Active)
            .Set(s => s.UpdatedAt, now)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task DowngradeSubscriptionToProAsync(int tenantId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.Tier, SubscriptionTier.Pro)
            .Set(s => s.Status, SubscriptionStatus.Active)
            .Set(s => s.UpdatedAt, now)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task DeactivateSubscriptionAsync(int tenantId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.Status, SubscriptionStatus.Canceled)
            .Set(s => s.UpdatedAt, now)
            .UpdateAsync(cancellationToken);
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
    public async Task<TenantSubscription> InsertSubscriptionAsync(TenantSubscription subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        subscription.Id = await _db.InsertWithInt32IdentityAsync(subscription, token: cancellationToken);

        return subscription;
    }

    /// <inheritdoc/>
    public async Task ReactivateFreeSubscriptionAsync(int subscriptionId, CancellationToken cancellationToken)
    {
        await _db.TenantSubscriptions
            .Where(s => s.Id == subscriptionId)
            .Set(s => s.Status, SubscriptionStatus.Active)
            .Set(s => s.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> UpdateSubscriptionAdminAsync(int tenantId, SubscriptionTier tier, SubscriptionStatus status, CancellationToken cancellationToken)
    {
        int updated = await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.Tier, tier)
            .Set(s => s.Status, status)
            .Set(s => s.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);

        return updated;
    }

    /// <inheritdoc/>
    public async Task<List<TenantSubscription>> GetSubscriptionsForTenantsAsync(List<int> tenantIds, CancellationToken cancellationToken)
    {
        List<TenantSubscription> subscriptions = await _db.TenantSubscriptions
            .Where(s => tenantIds.Contains(s.TenantId))
            .ToListAsync(cancellationToken);

        return subscriptions;
    }
}
