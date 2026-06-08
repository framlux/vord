// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for AlertEvaluationJob's per-rule-per-machine condition tracking. Replaces the
/// previous Redis-backed condition state. A row exists if and only if the rule's condition is
/// currently true for the given machine; it is removed when the condition clears, when the alert
/// fires after the DurationMinutes window, or when the rule is deleted.
/// </summary>
public interface IAlertConditionStateRepository
{
    /// <summary>Returns the condition state for the given rule and machine, or null if none exists.</summary>
    Task<AlertConditionState?> GetAsync(int alertRuleId, long machineId, CancellationToken cancellationToken);

    /// <summary>
    /// Upserts the condition state for the given rule and machine. If no row exists, a new one is
    /// inserted with FirstTriggeredAt and LastObservedAt both set to <paramref name="now"/>. If a
    /// row exists, only LastObservedAt is updated; the original FirstTriggeredAt is preserved.
    /// </summary>
    /// <returns>The FirstTriggeredAt value after the upsert.</returns>
    Task<DateTimeOffset> UpsertObservationAsync(int alertRuleId, long machineId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Deletes the condition state row for the given rule and machine (no-op if missing).</summary>
    Task DeleteAsync(int alertRuleId, long machineId, CancellationToken cancellationToken);

    /// <summary>Deletes every condition state row for the given rule (used on rule delete).</summary>
    Task DeleteForRuleAsync(int alertRuleId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes condition state rows whose LastObservedAt is older than <paramref name="olderThan"/>.
    /// Used by the daily reaper to garbage-collect rows orphaned when machines are unassigned from
    /// rules (the evaluation loop no longer iterates them so neither the cleared-condition nor the
    /// fired-event cleanup paths run).
    /// </summary>
    /// <returns>The number of rows deleted.</returns>
    Task<int> DeleteStaleAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
}
