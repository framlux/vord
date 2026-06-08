// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;

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
    public async Task<DateTimeOffset> UpsertObservationAsync(int alertRuleId, long machineId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Atomic insert-or-update. The unique index on (AlertRuleId, MachineId) guarantees at
        // most one row exists per pair; under contention one INSERT wins and the loser falls
        // through to the UPDATE branch. Returns the stable FirstTriggeredAt for the surviving
        // row so duration-window calculations remain anchored to the original trigger time.
        try
        {
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
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            AlertConditionState existing = await _db.AlertConditionStates
                .Where(s => (s.AlertRuleId == alertRuleId) && (s.MachineId == machineId))
                .FirstAsync(cancellationToken);

            await _db.AlertConditionStates
                .Where(s => s.Id == existing.Id)
                .Set(s => s.LastObservedAt, now)
                .UpdateAsync(cancellationToken);

            return existing.FirstTriggeredAt;
        }
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
    public async Task<int> DeleteStaleAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        return await _db.AlertConditionStates
            .Where(s => s.LastObservedAt < olderThan)
            .DeleteAsync(cancellationToken);
    }
}
