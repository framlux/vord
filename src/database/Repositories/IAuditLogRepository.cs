// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for audit log operations.
/// </summary>
public interface IAuditLogRepository
{
    /// <summary>
    /// Inserts an audit log entry into the database.
    /// </summary>
    /// <param name="entry">The audit log entry to insert</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns an awaitable Task</returns>
    Task InsertAuditLogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns paginated audit log entries for a tenant with optional filters, ordered by timestamp descending.
    /// Eager-loads the User navigation property.
    /// </summary>
    Task<List<AuditLogEntry>> GetAuditLogEntriesForTenantAsync(int tenantId, int skip, int take, AuditAction? actionFilter, DateTimeOffset? fromDate, DateTimeOffset? toDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of audit log entries matching the same filters.
    /// </summary>
    Task<int> CountAuditLogEntriesForTenantAsync(int tenantId, AuditAction? actionFilter, DateTimeOffset? fromDate, DateTimeOffset? toDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns paginated audit log entries with optional tenant filter, ordered by timestamp descending.
    /// Used by admin interfaces for cross-tenant audit log queries.
    /// </summary>
    /// <param name="tenantId">Optional tenant ID to filter by.</param>
    /// <param name="skip">Number of records to skip.</param>
    /// <param name="take">Number of records to take.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<(List<AuditLogEntry> Entries, int TotalCount)> QueryAuditLogEntriesAsync(int? tenantId, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a cursor-based batch of audit log entries for a tenant, ordered by ID ascending.
    /// Used for data export operations.
    /// </summary>
    /// <param name="tenantId">The tenant ID to filter by.</param>
    /// <param name="afterId">Return only entries with ID greater than this value.</param>
    /// <param name="batchSize">Maximum number of entries to return.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<AuditLogEntry>> GetAuditLogBatchAsync(int tenantId, long afterId, int batchSize, CancellationToken cancellationToken = default);
}
