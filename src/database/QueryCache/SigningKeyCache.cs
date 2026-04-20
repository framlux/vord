// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Database.Cache;

/// <summary>
/// Database cache operations for user signing keys.
/// </summary>
public partial class DatabaseCache
{
    /// <inheritdoc/>
    public async Task<UserSigningKey> CreateSigningKeyAsync(UserSigningKey key, CancellationToken cancellationToken = default)
    {
        int id = await _db.InsertWithInt32IdentityAsync(key, token: cancellationToken);
        key.Id = id;

        return key;
    }

    /// <inheritdoc/>
    public async Task<List<UserSigningKey>> GetSigningKeysForUserAsync(int userId, int tenantId, CancellationToken cancellationToken = default)
    {
        return await _db.UserSigningKeys
            .Where(k => k.UserId == userId && k.TenantId == tenantId)
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> GetActiveSigningKeyCountAsync(int userId, int tenantId, CancellationToken cancellationToken = default)
    {
        return await _db.UserSigningKeys
            .Where(k => k.UserId == userId &&
                        k.TenantId == tenantId &&
                        k.RevokedAt == null)
            .CountAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<UserSigningKey?> GetSigningKeyByIdAsync(int keyId, CancellationToken cancellationToken = default)
    {
        return await _db.UserSigningKeys
            .FirstOrDefaultAsync(k => k.Id == keyId, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task RevokeSigningKeyAsync(int keyId, int revokedByUserId, CancellationToken cancellationToken = default)
    {
        await _db.UserSigningKeys
            .Where(k => k.Id == keyId)
            .Set(k => k.RevokedAt, DateTimeOffset.UtcNow)
            .Set(k => k.RevokedByUserId, revokedByUserId)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<MachineAuthorizedKey> CreateMachineAuthorizationAsync(MachineAuthorizedKey key, CancellationToken cancellationToken = default)
    {
        int id = await _db.InsertWithInt32IdentityAsync(key, token: cancellationToken);
        key.Id = id;

        return key;
    }

    /// <inheritdoc/>
    public async Task<List<MachineAuthorizedKey>> GetAuthorizedKeysForMachineAsync(long machineId, CancellationToken cancellationToken = default)
    {
        return await _db.MachineAuthorizedKeys
            .Where(a => a.MachineId == machineId)
            .OrderByDescending(a => a.AuthorizedAt)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<UserSigningKey>> GetActiveSigningKeysForMachineAsync(long machineId, CancellationToken cancellationToken = default)
    {
        return await (
            from a in _db.MachineAuthorizedKeys
            join k in _db.UserSigningKeys on a.SigningKeyId equals k.Id
            where (a.MachineId == machineId) &&
                  (a.RevokedAt == null) &&
                  (k.RevokedAt == null)
            select k
        ).ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<bool> IsKeyAuthorizedForMachineAsync(int signingKeyId, long machineId, CancellationToken cancellationToken = default)
    {
        return await _db.MachineAuthorizedKeys
            .AnyAsync(a => (a.SigningKeyId == signingKeyId) &&
                           (a.MachineId == machineId) &&
                           (a.RevokedAt == null), cancellationToken);
    }

    /// <inheritdoc/>
    public async Task RevokeMachineAuthorizationAsync(long machineId, int signingKeyId, int revokedByUserId, CancellationToken cancellationToken = default)
    {
        await _db.MachineAuthorizedKeys
            .Where(a => (a.MachineId == machineId) &&
                        (a.SigningKeyId == signingKeyId) &&
                        (a.RevokedAt == null))
            .Set(a => a.RevokedAt, DateTimeOffset.UtcNow)
            .Set(a => a.RevokedByUserId, revokedByUserId)
            .UpdateAsync(cancellationToken);
    }
}
