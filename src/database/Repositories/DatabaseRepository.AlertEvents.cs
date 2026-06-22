// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IAlertEventRepository
{
    /// <inheritdoc/>
    public async Task<List<AlertEvent>> GetAlertEventsForTenantAsync(
        int tenantId,
        int skip,
        int take,
        AlertEventStatus? statusFilter,
        AlertSeverity? severityFilter,
        CancellationToken cancellationToken)
    {
        IQueryable<AlertEvent> query = _db.AlertEvents
            .LoadWith(e => e.AlertRule)
            .Where(e => e.TenantId == tenantId);

        if (statusFilter.HasValue)
        {
            query = query.Where(e => e.Status == statusFilter.Value);
        }

        if (severityFilter.HasValue)
        {
            query = query.Where(e => e.Severity == severityFilter.Value);
        }

        List<AlertEvent> events = await query
            .OrderByDescending(e => e.TriggeredAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return events;
    }

    /// <inheritdoc/>
    public async Task<int> CountAlertEventsForTenantAsync(
        int tenantId,
        AlertEventStatus? statusFilter,
        AlertSeverity? severityFilter,
        CancellationToken cancellationToken)
    {
        IQueryable<AlertEvent> query = _db.AlertEvents
            .Where(e => e.TenantId == tenantId);

        if (statusFilter.HasValue)
        {
            query = query.Where(e => e.Status == statusFilter.Value);
        }

        if (severityFilter.HasValue)
        {
            query = query.Where(e => e.Severity == severityFilter.Value);
        }

        int count = await query.CountAsync(cancellationToken);

        return count;
    }

    /// <inheritdoc/>
    public async Task<AlertEvent?> GetAlertEventByIdAsync(long eventId, int tenantId, CancellationToken cancellationToken)
    {
        AlertEvent? alertEvent = await _db.AlertEvents
            .FirstOrDefaultAsync(e => (e.Id == eventId) && (e.TenantId == tenantId), cancellationToken);

        return alertEvent;
    }

    /// <inheritdoc/>
    public async Task<AlertEvent?> GetAlertEventByIdAsync(long eventId, CancellationToken cancellationToken)
    {
        AlertEvent? alertEvent = await _db.AlertEvents
            .FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);

        return alertEvent;
    }

    /// <inheritdoc/>
    public async Task AcknowledgeAlertEventAsync(long eventId, int? userId, CancellationToken cancellationToken)
    {
        await _db.AlertEvents
            .Where(e => e.Id == eventId)
            .Set(e => e.Status, AlertEventStatus.Acknowledged)
            .Set(e => e.AcknowledgedAt, DateTimeOffset.UtcNow)
            .Set(e => e.AcknowledgedByUserId, userId)
            .UpdateAsync(cancellationToken);

        _logger.LogDebug("Acknowledged alert event {EventId} by user {UserId}", eventId, userId);
    }

    /// <inheritdoc/>
    public async Task ResolveEventsForRuleMachineAsync(int ruleId, long machineId, CancellationToken cancellationToken)
    {
        int resolved = await _db.AlertEvents
            .Where(e => (e.AlertRuleId == ruleId) &&
                        (e.MachineId == machineId) &&
                        (e.Status != AlertEventStatus.Resolved))
            .Set(e => e.Status, AlertEventStatus.Resolved)
            .Set(e => e.ResolvedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);

        if (resolved > 0)
        {
            _logger.LogDebug("Resolved {Count} active events for rule {RuleId} and machine {MachineId}", resolved, ruleId, machineId);
        }
    }

    /// <inheritdoc/>
    public async Task ResolveEventsForRuleAsync(int ruleId, CancellationToken cancellationToken)
    {
        int resolved = await _db.AlertEvents
            .Where(e => (e.AlertRuleId == ruleId) && (e.Status != AlertEventStatus.Resolved))
            .Set(e => e.Status, AlertEventStatus.Resolved)
            .Set(e => e.ResolvedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);

        if (resolved > 0)
        {
            _logger.LogInformation("Resolved {Count} active events for rule {RuleId}", resolved, ruleId);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> HasActiveEventForRuleMachineAsync(int ruleId, long machineId, CancellationToken cancellationToken)
    {
        bool hasActive = await _db.AlertEvents
            .AnyAsync(e => (e.AlertRuleId == ruleId) &&
                          (e.MachineId == machineId) &&
                          (e.Status != AlertEventStatus.Resolved), cancellationToken);

        return hasActive;
    }

    /// <inheritdoc/>
    public async Task ResolveEventsForMachineByMetricAsync(long machineId, AlertMetric metric, CancellationToken cancellationToken)
    {
        int resolved = await (from e in _db.AlertEvents
                              join r in _db.AlertRules on e.AlertRuleId equals r.Id
                              where (e.MachineId == machineId) &&
                                    (r.Metric == metric) &&
                                    (e.Status != AlertEventStatus.Resolved)
                              select e)
            .Set(e => e.Status, AlertEventStatus.Resolved)
            .Set(e => e.ResolvedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);

        if (resolved > 0)
        {
            _logger.LogDebug("Resolved {Count} active {Metric} events for machine {MachineId}", resolved, metric, machineId);
        }
    }

    /// <inheritdoc/>
    public async Task<AlertEvent?> CreateEventIfNotExistsAsync(AlertEvent alertEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alertEvent);

        await using DataConnectionTransaction tx = await _db.BeginTransactionAsync(cancellationToken);

        // Acquire advisory lock on PostgreSQL to prevent concurrent duplicate inserts.
        // Uses the two-argument form: key1=ruleId (int32), key2=machineId folded to int32 via XOR
        // so all 64 bits of machineId contribute to the lock key.
        if (_db.DataProvider.Name.Contains("PostgreSQL"))
        {
            int foldedMachineId = (int)(alertEvent.MachineId ^ (alertEvent.MachineId >> 32));
            await _db.ExecuteAsync(
                "SELECT pg_advisory_xact_lock(@ruleId, @machineId)",
                cancellationToken,
                new DataParameter("@ruleId", alertEvent.AlertRuleId),
                new DataParameter("@machineId", foldedMachineId));
        }

        // Deduplication check: skip insert if an active event already exists for this rule and machine.
        bool hasActive = await _db.AlertEvents
            .AnyAsync(e => (e.AlertRuleId == alertEvent.AlertRuleId) &&
                          (e.MachineId == alertEvent.MachineId) &&
                          (e.Status != AlertEventStatus.Resolved), cancellationToken);

        if (hasActive)
        {
            await tx.CommitAsync(cancellationToken);

            return null;
        }

        alertEvent.Id = await _db.InsertWithInt64IdentityAsync(alertEvent, token: cancellationToken);

        _logger.LogInformation("Created alert event {EventId} for rule {RuleId} and machine {MachineId}", alertEvent.Id, alertEvent.AlertRuleId, alertEvent.MachineId);

        await tx.CommitAsync(cancellationToken);

        return alertEvent;
    }

    /// <summary>Maximum rows returned per re-drive call; bounds work done per evaluation tick.</summary>
    private const int RedrivePageSize = 500;

    /// <inheritdoc/>
    public async Task<List<AlertEvent>> GetTriggeredEventsWithoutDeliveryAttemptsAsync(
        DateTimeOffset triggeredNotAfter,
        DateTimeOffset triggeredNotBefore,
        CancellationToken cancellationToken)
    {
        // Anti-join: return Triggered events in the window that have no IntegrationDeliveryAttempt row.
        // NOT EXISTS is used so the query returns events for tenants that have zero integrations only
        // within the bounded time window — the triggeredNotBefore cutoff prevents perpetual re-driving
        // of events from tenants that have never configured an integration.
        IQueryable<AlertEvent> query = from e in _db.AlertEvents
                                       where e.Status == AlertEventStatus.Triggered
                                             && e.TriggeredAt <= triggeredNotAfter
                                             && e.TriggeredAt >= triggeredNotBefore
                                             && !_db.IntegrationDeliveryAttempts.Any(a => a.AlertEventId == e.Id)
                                       orderby e.TriggeredAt
                                       select e;

        List<AlertEvent> events = await query.Take(RedrivePageSize).ToListAsync(cancellationToken);

        if (events.Count == RedrivePageSize)
        {
            _logger.LogWarning(
                "GetTriggeredEventsWithoutDeliveryAttemptsAsync returned the page cap of {Cap} rows; orphaned events may exist beyond the page boundary",
                RedrivePageSize);
        }

        return events;
    }
}
