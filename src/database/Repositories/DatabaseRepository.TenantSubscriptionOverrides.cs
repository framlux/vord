// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : ITenantSubscriptionOverrideRepository
{
    /// <inheritdoc/>
    public async Task<TenantSubscriptionOverride?> GetOverrideForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        TenantSubscriptionOverride? overrideRecord = await _db.TenantSubscriptionOverrides
            .Where(o => o.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

        return overrideRecord;
    }

    /// <inheritdoc/>
    public async Task UpsertOverrideAsync(int tenantId, int? machineLimit, int? retentionDays, int? alertRuleLimit, int? webhookLimit, CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Attempt update first (more likely in steady state)
        int updated = await _db.TenantSubscriptionOverrides
            .Where(o => o.TenantId == tenantId)
            .Set(o => o.MachineLimit, machineLimit)
            .Set(o => o.RetentionDays, retentionDays)
            .Set(o => o.AlertRuleLimit, alertRuleLimit)
            .Set(o => o.WebhookLimit, webhookLimit)
            .Set(o => o.UpdatedAt, now)
            .UpdateAsync(cancellationToken);

        if (updated > 0)
        {
            return;
        }

        // No existing row — insert new override
        try
        {
            await _db.InsertAsync(new TenantSubscriptionOverride
            {
                TenantId = tenantId,
                MachineLimit = machineLimit,
                RetentionDays = retentionDays,
                AlertRuleLimit = alertRuleLimit,
                WebhookLimit = webhookLimit,
                CreatedAt = now,
                UpdatedAt = now,
            }, token: cancellationToken);
        }
        catch (System.Data.Common.DbException)
        {
            // Unique constraint violation from race condition: another request inserted first. Retry as update.
            await _db.TenantSubscriptionOverrides
                .Where(o => o.TenantId == tenantId)
                .Set(o => o.MachineLimit, machineLimit)
                .Set(o => o.RetentionDays, retentionDays)
                .Set(o => o.AlertRuleLimit, alertRuleLimit)
                .Set(o => o.WebhookLimit, webhookLimit)
                .Set(o => o.UpdatedAt, now)
                .UpdateAsync(cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task RemoveOverrideAsync(int tenantId, CancellationToken cancellationToken)
    {
        await _db.TenantSubscriptionOverrides
            .Where(o => o.TenantId == tenantId)
            .DeleteAsync(cancellationToken);
    }
}
