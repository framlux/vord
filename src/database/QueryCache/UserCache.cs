// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Database.Cache;

/// <inheritdoc/>
public partial class DatabaseCache : IDatabaseCache
{
    /// <inheritdoc/>
    public async Task<UserAccount?> GetUserByExternalIdAsync(string externalId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        UserAccount? user = await _db.UserAccounts
            .Where(u => u.ExternalId == externalId && u.IsActive)
            .FirstOrDefaultAsync(cancellationToken);

        return user;
    }

    /// <inheritdoc/>
    public async Task<UserAccount> CreateUserAccountAsync(UserAccount user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        int newUserId = await InternalCreateUserAccountAsync(user, cancellationToken);
        user.Id = newUserId;

        return user;
    }

    private Task<int> InternalCreateUserAccountAsync(UserAccount user, CancellationToken cancellationToken)
        => _db.InsertWithInt32IdentityAsync(user, token: cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> DoAnyUsersExistAsync(CancellationToken cancellationToken)
    {
        bool anyUsersExist = await _db.UserAccounts
            .Where(u => u.IsSystem == false)
            .AnyAsync(u => u.IsActive, cancellationToken);

        return anyUsersExist;
    }

    /// <inheritdoc/>
    public async Task UpdateUserEmailAsync(int userId, string newEmail, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newEmail);

        await _db.UserAccounts
            .Where(u => u.Id == userId)
            .Set(u => u.Username, newEmail)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateUserAuthProviderAsync(int userId, AuthProviderType authProvider, CancellationToken cancellationToken)
    {
        await _db.UserAccounts
            .Where(u => u.Id == userId)
            .Set(u => u.AuthProvider, authProvider)
            .UpdateAsync(cancellationToken);
    }
}
