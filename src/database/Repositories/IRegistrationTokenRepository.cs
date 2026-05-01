// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for registration token operations.
/// </summary>
public interface IRegistrationTokenRepository
{
    /// <summary>
    /// Returns a registration token by its SHA-256 hash, or null if not found.
    /// </summary>
    /// <param name="tokenHash">The SHA-256 hash of the plaintext registration token.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<RegistrationToken?> GetTokenByHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new registration token and sets its generated ID.
    /// </summary>
    Task<RegistrationToken> CreateRegistrationTokenAsync(RegistrationToken token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes a registration token by setting its revoked flag and timestamp.
    /// Returns the number of rows updated (0 if token not found or already revoked).
    /// </summary>
    Task<int> RevokeRegistrationTokenAsync(long tokenId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of registration tokens for a tenant, ordered by creation date descending.
    /// </summary>
    Task<List<RegistrationToken>> GetRegistrationTokensForTenantAsync(int tenantId, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the total count of registration tokens for a tenant.
    /// </summary>
    Task<int> CountRegistrationTokensForTenantAsync(int tenantId, CancellationToken cancellationToken = default);
}
