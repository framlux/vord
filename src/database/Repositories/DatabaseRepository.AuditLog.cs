// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IAuditLogRepository
{
    /// <inheritdoc/>
    public async Task InsertAuditLogAsync(AuditLogEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Populate the client IP from the request context when the entry does not already
        // carry an explicit address. An explicit non-null value always takes priority, so
        // callers that supply their own IP are unaffected. Worker and background-job paths
        // receive null from NullAuditContextAccessor.
        entry.IpAddress ??= _auditContext.GetClientIp();

        await _db.InsertAsync(entry, token: cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<AuditLogEntry>> GetAuditLogEntriesForTenantAsync(
        int tenantId, int skip, int take,
        Database.Enums.AuditAction? actionFilter, DateTimeOffset? fromDate, DateTimeOffset? toDate,
        CancellationToken cancellationToken)
    {
        IQueryable<AuditLogEntry> query = _db.AuditLog
            .LoadWith(a => a.User)
            .Where(a => a.TenantId == tenantId);

        if (actionFilter.HasValue)
        {
            query = query.Where(a => a.Action == actionFilter.Value);
        }

        if (fromDate.HasValue)
        {
            query = query.Where(a => a.Timestamp >= fromDate.Value);
        }

        if (toDate.HasValue)
        {
            query = query.Where(a => a.Timestamp <= toDate.Value);
        }

        List<AuditLogEntry> entries = await query
            .OrderByDescending(a => a.Timestamp)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return entries;
    }

    /// <inheritdoc/>
    public async Task<int> CountAuditLogEntriesForTenantAsync(
        int tenantId,
        Database.Enums.AuditAction? actionFilter, DateTimeOffset? fromDate, DateTimeOffset? toDate,
        CancellationToken cancellationToken)
    {
        IQueryable<AuditLogEntry> query = _db.AuditLog
            .Where(a => a.TenantId == tenantId);

        if (actionFilter.HasValue)
        {
            query = query.Where(a => a.Action == actionFilter.Value);
        }

        if (fromDate.HasValue)
        {
            query = query.Where(a => a.Timestamp >= fromDate.Value);
        }

        if (toDate.HasValue)
        {
            query = query.Where(a => a.Timestamp <= toDate.Value);
        }

        int count = await query.CountAsync(cancellationToken);

        return count;
    }

    /// <inheritdoc/>
    public async Task<(List<AuditLogEntry> Entries, int TotalCount)> QueryAuditLogEntriesAsync(int? tenantId, int skip, int take, CancellationToken cancellationToken)
    {
        IQueryable<AuditLogEntry> query = _db.AuditLog;

        if (tenantId.HasValue)
        {
            query = query.Where(e => e.TenantId == tenantId.Value);
        }

        int totalCount = await query.CountAsync(cancellationToken);

        List<AuditLogEntry> entries = await query
            .OrderByDescending(e => e.Timestamp)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return (entries, totalCount);
    }

    /// <inheritdoc/>
    public async Task<List<AuditLogEntry>> GetAuditLogBatchAsync(int tenantId, long afterId, int batchSize, CancellationToken cancellationToken)
    {
        List<AuditLogEntry> entries = await _db.AuditLog
            .Where(a => (a.TenantId == tenantId) && (a.Id > afterId))
            .OrderBy(a => a.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        return entries;
    }
}
