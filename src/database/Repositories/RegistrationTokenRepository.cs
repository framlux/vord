// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IRegistrationTokenRepository
{
    /// <inheritdoc/>
    public async Task<RegistrationToken?> GetTokenByHashAsync(string tokenHash, CancellationToken cancellationToken)
    {
        RegistrationToken? token = await _db.RegistrationTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

        return token;
    }

    /// <inheritdoc/>
    public async Task<RegistrationToken> CreateRegistrationTokenAsync(RegistrationToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        token.Id = await _db.InsertWithInt64IdentityAsync(token, token: cancellationToken);

        _logger.LogDebug("Created registration token {TokenId} for tenant {TenantId}", token.Id, token.TenantId);

        return token;
    }

    /// <inheritdoc/>
    public async Task<int> RevokeRegistrationTokenAsync(long tokenId, int tenantId, CancellationToken cancellationToken)
    {
        int updated = await _db.RegistrationTokens
            .Where(t => t.Id == tokenId && t.TenantId == tenantId && t.IsRevoked == false)
            .Set(t => t.IsRevoked, true)
            .Set(t => t.RevokedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);

        if (updated > 0)
        {
            _logger.LogInformation("Revoked registration token {TokenId} for tenant {TenantId}", tokenId, tenantId);
        }

        return updated;
    }

    /// <inheritdoc/>
    public async Task<List<RegistrationToken>> GetRegistrationTokensForTenantAsync(int tenantId, int skip, int take, CancellationToken cancellationToken)
    {
        List<RegistrationToken> tokens = await _db.RegistrationTokens
            .Where(t => t.TenantId == tenantId)
            .OrderByDescending(t => t.CreatedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return tokens;
    }

    /// <inheritdoc/>
    public async Task<int> CountRegistrationTokensForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        int count = await _db.RegistrationTokens
            .Where(t => t.TenantId == tenantId)
            .CountAsync(cancellationToken);

        return count;
    }
}
