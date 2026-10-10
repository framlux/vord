// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
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
    /// row exists, LastObservedAt is advanced and the original FirstTriggeredAt is preserved, unless
    /// the row was last observed more than <paramref name="observationGap"/> before
    /// <paramref name="now"/>: the condition was not being watched in between, so the stretch restarts
    /// and FirstTriggeredAt becomes <paramref name="now"/> too.
    /// </summary>
    /// <param name="alertRuleId">The rule whose condition is being tracked.</param>
    /// <param name="machineId">The machine the condition was observed on.</param>
    /// <param name="now">The instant of this observation.</param>
    /// <param name="observationGap">
    /// The longest time a row may go unobserved and still be the same stretch. Supplied by the caller,
    /// which knows the evaluation cadence.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The FirstTriggeredAt value after the upsert.</returns>
    Task<DateTimeOffset> UpsertObservationAsync(int alertRuleId, long machineId, DateTimeOffset now, TimeSpan observationGap, CancellationToken cancellationToken);

    /// <summary>Deletes the condition state row for the given rule and machine (no-op if missing).</summary>
    Task DeleteAsync(int alertRuleId, long machineId, CancellationToken cancellationToken);

    /// <summary>Deletes every condition state row for the given rule (used on rule delete).</summary>
    Task DeleteForRuleAsync(int alertRuleId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the condition state rows of every machine of the tenant that the cutoff pauses. The
    /// rows carry no tenant of their own, so the tenant scope comes from the machine.
    /// </summary>
    /// <param name="tenantId">The tenant that owns the machines.</param>
    /// <param name="kind">Which machines are paused: none, all, or those with an id above the cutoff.</param>
    /// <param name="cutoffId">The last active machine id; read only when <paramref name="kind"/> is <see cref="QuotaCutoffKind.After"/>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows deleted.</returns>
    Task<int> DeleteForMachinesAfterAsync(int tenantId, QuotaCutoffKind kind, long cutoffId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes condition state rows whose LastObservedAt is older than <paramref name="olderThan"/>.
    /// Used by the daily reaper to garbage-collect rows orphaned when machines are unassigned from
    /// rules (the evaluation loop no longer iterates them so neither the cleared-condition nor the
    /// fired-event cleanup paths run).
    /// </summary>
    /// <returns>The number of rows deleted.</returns>
    Task<int> DeleteStaleAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
}
