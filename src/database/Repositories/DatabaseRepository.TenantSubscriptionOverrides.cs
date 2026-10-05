// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
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
    public async Task UpsertOverrideAsync(int tenantId, int? machineLimit, int? retentionDays, int? alertRuleLimit, int? webhookLimit, int? memberLimit, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Attempt update first (more likely in steady state)
        int updated = await _db.TenantSubscriptionOverrides
            .Where(o => o.TenantId == tenantId)
            .Set(o => o.MachineLimit, machineLimit)
            .Set(o => o.RetentionDays, retentionDays)
            .Set(o => o.AlertRuleLimit, alertRuleLimit)
            .Set(o => o.WebhookLimit, webhookLimit)
            .Set(o => o.MemberLimit, memberLimit)
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
                MemberLimit = memberLimit,
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
                .Set(o => o.MemberLimit, memberLimit)
                .Set(o => o.UpdatedAt, now)
                .UpdateAsync(cancellationToken);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> UpsertOverrideUnlessEnterpriseAsync(int tenantId, int? machineLimit, int? retentionDays, int? alertRuleLimit, int? webhookLimit, int? memberLimit, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IQueryable<TenantSubscription> enterpriseSubscription = EnterpriseSubscriptionOf(tenantId);

        int updated = await _db.TenantSubscriptionOverrides
            .Where(o => (o.TenantId == tenantId) && (enterpriseSubscription.Any() == false))
            .Set(o => o.MachineLimit, machineLimit)
            .Set(o => o.RetentionDays, retentionDays)
            .Set(o => o.AlertRuleLimit, alertRuleLimit)
            .Set(o => o.WebhookLimit, webhookLimit)
            .Set(o => o.MemberLimit, memberLimit)
            .Set(o => o.UpdatedAt, now)
            .UpdateAsync(cancellationToken);

        if (updated > 0)
        {
            return true;
        }

        // No row was updated: either the tenant has no override yet or it is on Enterprise. The insert
        // selects the tenant's own row, filtered by the same Enterprise test, so it yields nothing for
        // an Enterprise tenant and the two cases are told apart by the statement, not by a prior read.
        try
        {
            int inserted = await _db.Tenants
                .Where(t => (t.Id == tenantId) && (enterpriseSubscription.Any() == false))
                .InsertAsync(
                    _db.TenantSubscriptionOverrides,
                    t => new TenantSubscriptionOverride
                    {
                        TenantId = t.Id,
                        MachineLimit = machineLimit,
                        RetentionDays = retentionDays,
                        AlertRuleLimit = alertRuleLimit,
                        WebhookLimit = webhookLimit,
                        MemberLimit = memberLimit,
                        CreatedAt = now,
                        UpdatedAt = now,
                    },
                    cancellationToken);

            if (inserted > 0)
            {
                return true;
            }

            // Nothing inserted: the tenant is on Enterprise, or there is no such tenant to hold an
            // override. Only the first is a refusal.
            if (await enterpriseSubscription.AnyAsync(cancellationToken))
            {
                return false;
            }

            throw new InvalidOperationException($"Tenant {tenantId} does not exist.");
        }
        catch (System.Data.Common.DbException)
        {
            // Another request inserted the row first (unique constraint on the tenant). Retry as a
            // guarded update; if that also changes nothing the failure was not that race, so it is
            // surfaced rather than reported as an Enterprise refusal.
            int retried = await _db.TenantSubscriptionOverrides
                .Where(o => (o.TenantId == tenantId) && (enterpriseSubscription.Any() == false))
                .Set(o => o.MachineLimit, machineLimit)
                .Set(o => o.RetentionDays, retentionDays)
                .Set(o => o.AlertRuleLimit, alertRuleLimit)
                .Set(o => o.WebhookLimit, webhookLimit)
                .Set(o => o.MemberLimit, memberLimit)
                .Set(o => o.UpdatedAt, now)
                .UpdateAsync(cancellationToken);

            if (retried > 0)
            {
                return true;
            }

            if (await enterpriseSubscription.AnyAsync(cancellationToken))
            {
                return false;
            }

            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<List<TenantSubscriptionOverride>> GetOverridesForTenantsAsync(
        List<int> tenantIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);

        return await _db.TenantSubscriptionOverrides
            .Where(o => tenantIds.Contains(o.TenantId))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task RemoveOverrideAsync(int tenantId, CancellationToken cancellationToken)
    {
        await _db.TenantSubscriptionOverrides
            .Where(o => o.TenantId == tenantId)
            .DeleteAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveOverrideUnlessEnterpriseAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        IQueryable<TenantSubscription> enterpriseSubscription = EnterpriseSubscriptionOf(tenantId);

        int deleted = await _db.TenantSubscriptionOverrides
            .Where(o => (o.TenantId == tenantId) && (enterpriseSubscription.Any() == false))
            .DeleteAsync(cancellationToken);

        if (deleted > 0)
        {
            return true;
        }

        // Nothing was deleted: the tenant either had no override to remove, which is a successful
        // no-op, or is on Enterprise and the guard held.
        bool isEnterprise = await enterpriseSubscription.AnyAsync(cancellationToken);

        return isEnterprise == false;
    }

    /// <summary>
    /// The tenant's subscription row when, and only when, it is on the Enterprise tier. Used inside
    /// a write's WHERE clause so the Enterprise test and the write are one statement.
    /// </summary>
    private IQueryable<TenantSubscription> EnterpriseSubscriptionOf(int tenantId)
    {
        return _db.TenantSubscriptions
            .Where(s => (s.TenantId == tenantId) && (s.Tier == SubscriptionTier.Enterprise));
    }
}
