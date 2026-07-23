// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Tenant-deletion lifecycle persistence. See <see cref="ITenantDeletionRepository"/>.
/// </summary>
public partial class DatabaseRepository : ITenantDeletionRepository
{
    /// <inheritdoc/>
    public async Task<TenantDeletion?> GetActiveDeletionForTenantAsync(int tenantId, CancellationToken ct)
    {
        TenantDeletion? deletion = await _db.TenantDeletions
            .Where(d => (d.TenantId == tenantId) && (d.Status != TenantDeletionStatus.Restored))
            .OrderByDescending(d => d.Id)
            .FirstOrDefaultAsync(ct);

        return deletion;
    }

    /// <inheritdoc/>
    public async Task<TenantDeletion> InsertDeletionAsync(TenantDeletion deletion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deletion);

        int id = await _db.InsertWithInt32IdentityAsync(deletion, token: ct);
        deletion.Id = id;

        return deletion;
    }

    /// <inheritdoc/>
    public async Task<int> UpdateDeletionStatusAsync(int id, TenantDeletionStatus status, DateTimeOffset? purgedAt, CancellationToken ct)
    {
        int updated = await _db.TenantDeletions
            .Where(d => d.Id == id)
            .Set(d => d.Status, status)
            .Set(d => d.PurgedAt, purgedAt)
            .UpdateAsync(ct);

        return updated;
    }

    /// <inheritdoc/>
    public async Task<List<TenantDeletion>> GetDueDeletionsAsync(DateTimeOffset now, CancellationToken ct)
    {
        List<TenantDeletion> deletions = await _db.TenantDeletions
            .Where(d => (d.Status == TenantDeletionStatus.Deactivated) && (d.ScheduledPurgeAt <= now))
            .OrderBy(d => d.ScheduledPurgeAt)
            .ToListAsync(ct);

        return deletions;
    }

    /// <inheritdoc/>
    public async Task<(List<TenantDeletion> Deletions, int TotalCount)> ListDeletionsAsync(
        bool includeCompleted, int skip, int take, CancellationToken ct)
    {
        IQueryable<TenantDeletion> query = _db.TenantDeletions;
        if (includeCompleted == false)
        {
            query = query.Where(d => d.Status == TenantDeletionStatus.Deactivated);
        }

        int total = await query.CountAsync(ct);
        List<TenantDeletion> rows = await query
            .OrderByDescending(d => d.RequestedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return (rows, total);
    }
}
