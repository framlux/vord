// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Service for managing user signing keys used in remote command authorization.
/// </summary>
public interface ISigningKeyService
{
    /// <summary>
    /// Maximum number of active (non-revoked) signing keys per user per tenant.
    /// </summary>
    const int MaxActiveKeysPerUser = 5;

    /// <summary>
    /// Registers a new signing key for a user within a tenant.
    /// </summary>
    /// <param name="userId">The user registering the key</param>
    /// <param name="tenantId">The tenant scope</param>
    /// <param name="label">User-chosen label for the key</param>
    /// <param name="publicKeyBase64">Base64-encoded 32-byte Ed25519 public key</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns the created signing key, or null if at max key limit</returns>
    Task<ServiceResult<UserSigningKey>> RegisterKeyAsync(int userId, int tenantId, string label, string publicKeyBase64, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all signing keys for a user within a tenant.
    /// </summary>
    /// <param name="userId">The user ID</param>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns a list of signing keys</returns>
    Task<List<UserSigningKey>> ListKeysAsync(int userId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a signing key.
    /// </summary>
    /// <param name="keyId">The key ID to revoke</param>
    /// <param name="userId">The user performing the revocation</param>
    /// <param name="tenantId">The tenant scope for authorization</param>
    /// <param name="isAdminOrTenantAdmin">Whether the user has admin privileges to revoke others' keys</param>
    /// <param name="cancellationToken">Token used to cancel async calls</param>
    /// <returns>Returns a service result indicating success or failure</returns>
    Task<ServiceResult<bool>> RevokeKeyAsync(int keyId, int userId, int tenantId, bool isAdminOrTenantAdmin, CancellationToken cancellationToken = default);
}
