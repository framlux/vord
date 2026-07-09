// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for tenant invitation operations.
/// </summary>
public interface IInvitationRepository
{
    /// <summary>
    /// Creates a new tenant invitation in the database.
    /// </summary>
    Task<TenantInvitation> CreateInvitationAsync(TenantInvitation invitation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a tenant invitation by its unique token.
    /// </summary>
    Task<TenantInvitation?> GetInvitationByTokenAsync(string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all invitations for a tenant.
    /// </summary>
    Task<IEnumerable<TenantInvitation>> GetInvitationsForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a pending invitation for a specific email and tenant.
    /// </summary>
    Task<TenantInvitation?> GetPendingInvitationByEmailAndTenantAsync(string email, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the status of a tenant invitation, scoped to the owning tenant.
    /// </summary>
    /// <param name="invitationId">The invitation ID.</param>
    /// <param name="tenantId">The tenant that must own the invitation for the update to apply.</param>
    /// <param name="status">The new invitation status.</param>
    /// <param name="acceptedByUserId">The accepting user, when transitioning to Accepted; otherwise null.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><c>true</c> if a matching invitation in the tenant was updated; otherwise <c>false</c>.</returns>
    Task<bool> UpdateInvitationStatusAsync(int invitationId, int tenantId, InvitationStatus status, int? acceptedByUserId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a tenant invitation, scoped to the owning tenant.
    /// </summary>
    /// <param name="invitationId">The invitation ID.</param>
    /// <param name="tenantId">The tenant that must own the invitation for the revocation to apply.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><c>true</c> if a matching invitation in the tenant was revoked; otherwise <c>false</c>.</returns>
    Task<bool> RevokeInvitationAsync(int invitationId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts pending, non-expired invitations for a tenant as of the given instant. Expired pending
    /// invitations (ExpiresAt at or before asOf) are excluded.
    /// </summary>
    /// <param name="tenantId">The tenant to count pending invitations for.</param>
    /// <param name="asOf">The reference instant; invitations expiring at or before this value are excluded.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<int> CountPendingInvitationsAsync(int tenantId, DateTimeOffset asOf, CancellationToken cancellationToken = default);
}
