// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for alert event operations.
/// </summary>
public interface IAlertEventRepository
{
    /// <summary>
    /// Returns paginated alert events for a tenant with optional filters, ordered by TriggeredAt descending.
    /// Eager-loads the AlertRule navigation property.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="skip">Number of items to skip.</param>
    /// <param name="take">Number of items to take.</param>
    /// <param name="statusFilter">Optional status filter.</param>
    /// <param name="severityFilter">Optional severity filter.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<AlertEvent>> GetAlertEventsForTenantAsync(int tenantId, int skip, int take, AlertEventStatus? statusFilter, AlertSeverity? severityFilter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of alert events matching the same filters.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="statusFilter">Optional status filter.</param>
    /// <param name="severityFilter">Optional severity filter.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<int> CountAlertEventsForTenantAsync(int tenantId, AlertEventStatus? statusFilter, AlertSeverity? severityFilter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an alert event by ID scoped to a tenant, or null if not found.
    /// </summary>
    /// <param name="eventId">The alert event ID.</param>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<AlertEvent?> GetAlertEventByIdAsync(long eventId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an alert event by ID without tenant scoping, or null if not found.
    /// Used by background workers that process events across all tenants.
    /// </summary>
    /// <param name="eventId">The alert event ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<AlertEvent?> GetAlertEventByIdAsync(long eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Acknowledges an alert event by setting its status, timestamp, and user ID.
    /// </summary>
    /// <param name="eventId">The alert event ID.</param>
    /// <param name="userId">The user who acknowledged the event.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task AcknowledgeAlertEventAsync(long eventId, int? userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves all active (non-resolved) events for a specific rule and machine pair.
    /// </summary>
    /// <param name="ruleId">The alert rule ID.</param>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task ResolveEventsForRuleMachineAsync(int ruleId, long machineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves all active (non-resolved) events for a rule (used when deleting a rule).
    /// </summary>
    /// <param name="ruleId">The alert rule ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task ResolveEventsForRuleAsync(int ruleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns true if there is an active (non-resolved) event for a rule and machine pair.
    /// </summary>
    /// <param name="ruleId">The alert rule ID.</param>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<bool> HasActiveEventForRuleMachineAsync(int ruleId, long machineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates an alert event atomically with advisory lock deduplication protection.
    /// On PostgreSQL, acquires pg_advisory_xact_lock within a transaction to prevent duplicates.
    /// If an active event already exists for the rule and machine, returns null without inserting.
    /// On non-PostgreSQL (SQLite for tests), skips the advisory lock but still performs the dedup check.
    /// </summary>
    /// <param name="alertEvent">The alert event to create.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<AlertEvent?> CreateEventIfNotExistsAsync(AlertEvent alertEvent, CancellationToken cancellationToken = default);
}
