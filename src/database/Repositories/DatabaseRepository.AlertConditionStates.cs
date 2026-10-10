// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IAlertConditionStateRepository
{
    /// <inheritdoc/>
    public async Task<AlertConditionState?> GetAsync(int alertRuleId, long machineId, CancellationToken cancellationToken)
    {
        return await _db.AlertConditionStates
            .Where(s => (s.AlertRuleId == alertRuleId) && (s.MachineId == machineId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<DateTimeOffset> UpsertObservationAsync(int alertRuleId, long machineId, DateTimeOffset now, TimeSpan observationGap, CancellationToken cancellationToken)
    {
        // A row last observed before this instant was not being watched through the stretch since, so
        // its FirstTriggeredAt no longer describes a continuous condition.
        DateTimeOffset staleBefore = now - observationGap;

        // Single round-trip atomic upsert on Postgres. INSERT ... ON CONFLICT DO UPDATE ... RETURNING
        // returns the surviving row's FirstTriggeredAt so duration windows stay anchored to the
        // original trigger time, unless the gap rule restarted the stretch. The unique index on
        // (AlertRuleId, MachineId) is the conflict target. The right-hand side of every SET reads the
        // row as it was before the statement, so the gap test sees the old LastObservedAt.
        if (_db.DataProvider.Name.Contains("PostgreSQL"))
        {
            List<DateTimeOffset> firstTriggered = await _db.QueryToListAsync<DateTimeOffset>(
                """
                INSERT INTO "AlertConditionStates" ("AlertRuleId", "MachineId", "FirstTriggeredAt", "LastObservedAt")
                VALUES (@ruleId, @machineId, @now, @now)
                ON CONFLICT ("AlertRuleId", "MachineId")
                DO UPDATE SET
                    "FirstTriggeredAt" = CASE
                        WHEN "AlertConditionStates"."LastObservedAt" < @staleBefore THEN EXCLUDED."FirstTriggeredAt"
                        ELSE "AlertConditionStates"."FirstTriggeredAt"
                    END,
                    "LastObservedAt" = EXCLUDED."LastObservedAt"
                RETURNING "FirstTriggeredAt"
                """,
                new DataParameter("@ruleId", alertRuleId),
                new DataParameter("@machineId", machineId),
                new DataParameter("@now", now),
                new DataParameter("@staleBefore", staleBefore));

            return firstTriggered.Single();
        }

        // SQLite test path: insert-or-update without ON CONFLICT RETURNING.
        AlertConditionState? existing = await _db.AlertConditionStates
            .Where(s => (s.AlertRuleId == alertRuleId) && (s.MachineId == machineId))
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            if (existing.LastObservedAt < staleBefore)
            {
                await _db.AlertConditionStates
                    .Where(s => s.Id == existing.Id)
                    .Set(s => s.FirstTriggeredAt, now)
                    .Set(s => s.LastObservedAt, now)
                    .UpdateAsync(cancellationToken);

                return now;
            }

            await _db.AlertConditionStates
                .Where(s => s.Id == existing.Id)
                .Set(s => s.LastObservedAt, now)
                .UpdateAsync(cancellationToken);

            return existing.FirstTriggeredAt;
        }

        AlertConditionState row = new()
        {
            AlertRuleId = alertRuleId,
            MachineId = machineId,
            FirstTriggeredAt = now,
            LastObservedAt = now,
        };
        row.Id = await _db.InsertWithInt64IdentityAsync(row, token: cancellationToken);

        return now;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(int alertRuleId, long machineId, CancellationToken cancellationToken)
    {
        await _db.AlertConditionStates
            .Where(s => (s.AlertRuleId == alertRuleId) && (s.MachineId == machineId))
            .DeleteAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task DeleteForRuleAsync(int alertRuleId, CancellationToken cancellationToken)
    {
        await _db.AlertConditionStates
            .Where(s => s.AlertRuleId == alertRuleId)
            .DeleteAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteForMachinesAfterAsync(int tenantId, QuotaCutoffKind kind, long cutoffId, CancellationToken cancellationToken)
    {
        // The rows carry no tenant, so a machine row supplies the scope.
        IQueryable<AlertConditionState> tenantStates = _db.AlertConditionStates
            .Where(s => _db.Machines.Any(m => (m.Id == s.MachineId) && (m.TenantId == tenantId)));

        switch (kind)
        {
            case QuotaCutoffKind.None:
                return 0;
            case QuotaCutoffKind.All:
                break;
            case QuotaCutoffKind.After:
                tenantStates = tenantStates.Where(s => s.MachineId > cutoffId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown quota cutoff kind");
        }

        return await tenantStates.DeleteAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> DeleteStaleAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        return await _db.AlertConditionStates
            .Where(s => s.LastObservedAt < olderThan)
            .DeleteAsync(cancellationToken);
    }
}
