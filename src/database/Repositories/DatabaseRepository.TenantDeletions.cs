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

    /// <inheritdoc/>
    public async Task SetTenantActiveAsync(
        int tenantId, bool isActive, int? disabledByUserId, DateTimeOffset? disabledAt, CancellationToken ct)
    {
        await _db.Tenants
            .Where(t => t.Id == tenantId)
            .Set(t => t.IsActive, isActive)
            .Set(t => t.DisabledAt, disabledAt)
            .Set(t => t.DisabledByUserId, disabledByUserId)
            .UpdateAsync(ct);
    }

    /// <inheritdoc/>
    public async Task PurgeTenantOperationalDataAsync(int tenantId, CancellationToken ct)
    {
        // Child/leaf tables scoped through their parent's TenantId (they carry no TenantId column).
        await _db.MachineStateDetails
            .Where(x => _db.Machines.Any(m => (m.Id == x.MachineId) && (m.TenantId == tenantId)))
            .DeleteAsync(ct);
        await _db.AlertConditionStates
            .Where(x => _db.AlertRules.Any(r => (r.Id == x.AlertRuleId) && (r.TenantId == tenantId)))
            .DeleteAsync(ct);
        await _db.AlertRuleMachines
            .Where(x => _db.AlertRules.Any(r => (r.Id == x.AlertRuleId) && (r.TenantId == tenantId)))
            .DeleteAsync(ct);
        await _db.IntegrationDeliveryAttempts
            .Where(x => _db.IntegrationEndpoints.Any(e => (e.Id == x.IntegrationEndpointId) && (e.TenantId == tenantId)))
            .DeleteAsync(ct);
        await _db.AlertEmailDeliveryAttempts
            .Where(x => _db.AlertEvents.Any(e => (e.Id == x.AlertEventId) && (e.TenantId == tenantId)))
            .DeleteAsync(ct);

        // Tables carrying TenantId directly.
        await _db.MachineAuthorizedKeys.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.MachineStateSummaries.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.MachineTelemetry.Where(x => x.TenantId == tenantId).DeleteAsync(ct);        // partitioned
        await _db.AlertEvents.Where(x => x.TenantId == tenantId).DeleteAsync(ct);             // partitioned
        await _db.RemoteCommands.Where(x => x.TenantId == tenantId).DeleteAsync(ct);          // partitioned
        await _db.AlertRules.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.IntegrationEndpoints.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.RegistrationTokens.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.UserSigningKeys.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.DataExportJobs.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.Machines.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.TenantOidcConfigurations.Where(x => x.TenantId == tenantId).DeleteAsync(ct); // drops encrypted OIDC secret
        await _db.TenantInvitations.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.TenantSubscriptionOverrides.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        await _db.TenantSubscriptions.Where(x => x.TenantId == tenantId).DeleteAsync(ct);
        // UserTenantRoles for this tenant are removed by DeleteUserTenantRolesForTenantAsync in the job,
        // after the orphan computation reads them — not here.
    }

    /// <inheritdoc/>
    public async Task<List<int>> GetUserIdsWithAnyRoleInTenantAsync(int tenantId, CancellationToken ct)
    {
        return await _db.UserTenantRoles
            .Where(r => r.AssignedTenantId == tenantId)
            .Select(r => r.UserId)
            .Distinct()
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteUserTenantRolesForTenantAsync(int tenantId, CancellationToken ct)
    {
        await _db.UserTenantRoles.Where(r => r.AssignedTenantId == tenantId).DeleteAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<bool> UserHasAnyActiveRoleAsync(int userId, CancellationToken ct)
    {
        return await _db.UserTenantRoles
            .AnyAsync(r => (r.UserId == userId) && (r.IsActive == true), ct);
    }

    /// <inheritdoc/>
    public async Task<int> MaskUserAsync(int userId, CancellationToken ct)
    {
        // Masking is idempotent: an already-masked account carries the tombstone marker in ExternalId,
        // so re-running skips it (returns 0). Username becomes a per-id tombstone; the OIDC subject
        // (ExternalId) is replaced so a future signup with the same identity is a fresh account, never a
        // re-link. The row and its Id are kept so every AuditLog FK stays valid.
        string tombstone = $"deleted-user-{userId}";

        return await _db.UserAccounts
            .Where(u => (u.Id == userId) && (u.ExternalId.StartsWith("deleted-user-") == false))
            .Set(u => u.Username, tombstone)
            .Set(u => u.ExternalId, v => $"{tombstone}:{(short)v.AuthProvider}")
            .UpdateAsync(ct);
    }
}
