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

    /// <summary>
    /// Sets a tenant's <see cref="Tenant.IsActive"/> flag along with the disabling actor and
    /// timestamp. Used both to deactivate a tenant for deletion and to restore it.
    /// </summary>
    Task SetTenantActiveAsync(int tenantId, bool isActive, int? disabledByUserId, DateTimeOffset? disabledAt, CancellationToken ct);

    /// <summary>
    /// Purges a tenant's operational data — everything except <c>Tenants</c>, <c>UserAccounts</c>,
    /// <c>AuditLog</c>, and <c>UserTenantRoles</c> (the caller reads membership via
    /// <see cref="GetUserIdsWithAnyRoleInTenantAsync"/> before removing roles separately with
    /// <see cref="DeleteUserTenantRolesForTenantAsync"/>). Deletes follow the order of
    /// <c>InitialMigration.Down()</c>: children before parents. Tables with no <c>TenantId</c>
    /// column are scoped by a subquery on their parent's <c>TenantId</c>. Every delete predicate is
    /// scoped to <paramref name="tenantId"/> (directly or via subquery), so re-running this method
    /// on an already-purged tenant deletes zero rows and does not throw — it is idempotent.
    /// </summary>
    Task PurgeTenantOperationalDataAsync(int tenantId, CancellationToken ct);

    /// <summary>
    /// Gets the distinct set of user ids that have any <c>UserTenantRoles</c> row for the tenant,
    /// whether the role is currently active or has been disabled.
    /// </summary>
    Task<List<int>> GetUserIdsWithAnyRoleInTenantAsync(int tenantId, CancellationToken ct);

    /// <summary>
    /// Deletes all <c>UserTenantRoles</c> rows for the tenant. Called after the caller has read
    /// tenant membership, so it can determine which users become orphaned by the removal.
    /// </summary>
    Task DeleteUserTenantRolesForTenantAsync(int tenantId, CancellationToken ct);

    /// <summary>
    /// Determines whether a user has any active role in any tenant, across the whole system —
    /// used to decide whether the user account itself has become an orphan and should be masked.
    /// </summary>
    Task<bool> UserHasAnyActiveRoleAsync(int userId, CancellationToken ct);

    /// <summary>
    /// Masks the PII on a <see cref="UserAccount"/> row that has no remaining active tenant role,
    /// replacing <c>Username</c> and <c>ExternalId</c> with a per-id tombstone so the row (and its
    /// id, kept for <c>AuditLog</c> FK integrity) can no longer be linked back to the real identity
    /// or re-claimed by a future OIDC sign-in. Idempotent: a row whose <c>ExternalId</c> already
    /// carries the tombstone prefix is skipped. Returns the number of rows updated (0 or 1).
    /// </summary>
    Task<int> MaskUserAsync(int userId, CancellationToken ct);
}
