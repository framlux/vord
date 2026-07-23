// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Persistence for the tenant-deletion lifecycle. A row in <c>TenantDeletions</c> is created when
/// a tenant is deactivated for deletion, transitions to Purged once Phase 2 removes the tenant's
/// data, or transitions to Restored if an operator reverses the deactivation before the purge runs.
/// </summary>
public interface ITenantDeletionRepository
{
    /// <summary>
    /// Gets the non-Restored deletion row for a tenant (Deactivated or Purged), newest first, or
    /// null if the tenant has no active or completed deletion.
    /// </summary>
    Task<TenantDeletion?> GetActiveDeletionForTenantAsync(int tenantId, CancellationToken ct);

    /// <summary>
    /// Inserts a new deletion row and returns it with its assigned <see cref="TenantDeletion.Id"/>.
    /// </summary>
    Task<TenantDeletion> InsertDeletionAsync(TenantDeletion deletion, CancellationToken ct);

    /// <summary>
    /// Updates the status (and, when transitioning to Purged, the purge timestamp) of a deletion
    /// row. Returns the number of rows affected — 0 if <paramref name="id"/> does not exist.
    /// </summary>
    Task<int> UpdateDeletionStatusAsync(int id, TenantDeletionStatus status, DateTimeOffset? purgedAt, CancellationToken ct);

    /// <summary>
    /// Gets all Deactivated rows whose <see cref="TenantDeletion.ScheduledPurgeAt"/> is at or
    /// before <paramref name="now"/> — the work queue for the purge job.
    /// </summary>
    Task<List<TenantDeletion>> GetDueDeletionsAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Lists deletion rows for the admin panel, newest-requested first. When
    /// <paramref name="includeCompleted"/> is false, only Deactivated rows are returned; otherwise
    /// all rows are returned regardless of status.
    /// </summary>
    Task<(List<TenantDeletion> Deletions, int TotalCount)> ListDeletionsAsync(bool includeCompleted, int skip, int take, CancellationToken ct);
}
