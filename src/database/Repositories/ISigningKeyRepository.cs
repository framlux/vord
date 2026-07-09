// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for user signing key and machine authorization operations.
/// </summary>
public interface ISigningKeyRepository
{
    /// <summary>
    /// Creates a new user signing key in the database.
    /// </summary>
    Task<UserSigningKey> CreateSigningKeyAsync(UserSigningKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all signing keys for a user within a tenant.
    /// </summary>
    Task<List<UserSigningKey>> GetSigningKeysForUserAsync(int userId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of active (non-revoked) signing keys for a user within a tenant.
    /// </summary>
    Task<int> GetActiveSigningKeyCountAsync(int userId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a signing key by its ID.
    /// </summary>
    Task<UserSigningKey?> GetSigningKeyByIdAsync(int keyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a signing key scoped to the owning tenant.
    /// </summary>
    /// <param name="keyId">The signing key ID.</param>
    /// <param name="tenantId">The tenant that must own the key for the revocation to apply.</param>
    /// <param name="revokedByUserId">The user performing the revocation.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><c>true</c> if a matching key in the tenant was revoked; otherwise <c>false</c>.</returns>
    Task<bool> RevokeSigningKeyAsync(int keyId, int tenantId, int revokedByUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new machine authorization record in the database.
    /// </summary>
    Task<MachineAuthorizedKey> CreateMachineAuthorizationAsync(MachineAuthorizedKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all authorized key records for a machine, including active and revoked.
    /// </summary>
    Task<List<MachineAuthorizedKey>> GetAuthorizedKeysForMachineAsync(long machineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets active signing keys that are authorized for a specific machine.
    /// </summary>
    Task<List<UserSigningKey>> GetActiveSigningKeysForMachineAsync(long machineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a signing key has an active authorization for a machine.
    /// </summary>
    Task<bool> IsKeyAuthorizedForMachineAsync(int signingKeyId, long machineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a machine authorization by setting RevokedAt and RevokedByUserId on the active record,
    /// scoped to the owning tenant.
    /// </summary>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="signingKeyId">The signing key ID.</param>
    /// <param name="tenantId">The tenant that must own the authorization for the revocation to apply.</param>
    /// <param name="revokedByUserId">The user performing the revocation.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><c>true</c> if a matching active authorization in the tenant was revoked; otherwise <c>false</c>.</returns>
    Task<bool> RevokeMachineAuthorizationAsync(long machineId, int signingKeyId, int tenantId, int revokedByUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a previously-revoked authorization for a machine and signing key, or null if none exists.
    /// </summary>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="signingKeyId">The signing key ID.</param>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<MachineAuthorizedKey?> GetRevokedAuthorizationAsync(long machineId, int signingKeyId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-activates a previously revoked authorization by clearing revocation fields and updating
    /// authorization fields, scoped to the owning tenant.
    /// </summary>
    /// <param name="authorizationId">The authorization record ID.</param>
    /// <param name="tenantId">The tenant that must own the authorization for the re-activation to apply.</param>
    /// <param name="userId">The user re-activating the authorization.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><c>true</c> if a matching authorization in the tenant was re-activated; otherwise <c>false</c>.</returns>
    Task<bool> ReactivateAuthorizationAsync(int authorizationId, int tenantId, int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the active (non-revoked) authorization for a machine and signing key, or null if none exists.
    /// </summary>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="signingKeyId">The signing key ID.</param>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<MachineAuthorizedKey?> GetActiveAuthorizationAsync(long machineId, int signingKeyId, int tenantId, CancellationToken cancellationToken = default);
}
