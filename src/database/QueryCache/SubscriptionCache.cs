// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Database.Cache;

/// <inheritdoc/>
public partial class DatabaseCache : IDatabaseCache
{
    /// <inheritdoc/>
    public async Task UpdateSubscriptionOnCheckoutAsync(int tenantId, SubscriptionTier tier, int? alertRuleLimit, int? webhookLimit, CancellationToken cancellationToken)
    {
        int retentionDays = tier == SubscriptionTier.Team ? 365 : 30;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.Tier, tier)
            .Set(s => s.Status, SubscriptionStatus.Active)
            .Set(s => s.MachineLimit, (int?)null)
            .Set(s => s.RetentionDays, retentionDays)
            .Set(s => s.AlertRuleLimit, alertRuleLimit)
            .Set(s => s.WebhookLimit, webhookLimit)
            .Set(s => s.CancelAtPeriodEnd, false)
            .Set(s => s.PendingAction, PendingSubscriptionAction.None)
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
    public async Task RevertSubscriptionToFreeAsync(int tenantId, int machineLimit, int retentionDays, int alertRuleLimit, int webhookLimit, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.Tier, SubscriptionTier.Free)
            .Set(s => s.Status, SubscriptionStatus.Active)
            .Set(s => s.MachineLimit, machineLimit)
            .Set(s => s.RetentionDays, retentionDays)
            .Set(s => s.AlertRuleLimit, alertRuleLimit)
            .Set(s => s.WebhookLimit, webhookLimit)
            .Set(s => s.CancelAtPeriodEnd, false)
            .Set(s => s.PendingAction, PendingSubscriptionAction.None)
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
    public async Task DowngradeSubscriptionToProAsync(int tenantId, int? alertRuleLimit, int? webhookLimit, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.Tier, SubscriptionTier.Pro)
            .Set(s => s.Status, SubscriptionStatus.Active)
            .Set(s => s.MachineLimit, (int?)null)
            .Set(s => s.RetentionDays, 30)
            .Set(s => s.AlertRuleLimit, alertRuleLimit)
            .Set(s => s.WebhookLimit, webhookLimit)
            .Set(s => s.CancelAtPeriodEnd, false)
            .Set(s => s.PendingAction, PendingSubscriptionAction.None)
            .Set(s => s.UpdatedAt, now)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task SetCancelAtPeriodEndAsync(int tenantId, bool cancelAtPeriodEnd, PendingSubscriptionAction pendingAction, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _db.TenantSubscriptions
            .Where(s => s.TenantId == tenantId)
            .Set(s => s.CancelAtPeriodEnd, cancelAtPeriodEnd)
            .Set(s => s.PendingAction, pendingAction)
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
            .Set(s => s.CancelAtPeriodEnd, false)
            .Set(s => s.PendingAction, PendingSubscriptionAction.None)
            .Set(s => s.UpdatedAt, now)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<TenantSubscription>> GetPendingCancellationsAsync(CancellationToken cancellationToken)
    {
        List<TenantSubscription> subscriptions = await _db.TenantSubscriptions
            .Where(s => s.CancelAtPeriodEnd == true && s.Status == SubscriptionStatus.Active)
            .ToListAsync(cancellationToken);

        return subscriptions;
    }

    /// <inheritdoc/>
    public async Task<List<TenantSubscription>> GetPaidSubscriptionsAsync(CancellationToken cancellationToken)
    {
        List<TenantSubscription> subscriptions = await _db.TenantSubscriptions
            .Where(s => s.Tier != SubscriptionTier.Free)
            .ToListAsync(cancellationToken);

        return subscriptions;
    }
}

