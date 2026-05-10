// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IUserRepository
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

    /// <inheritdoc/>
    public async Task<UserAccount?> GetUserByIdAsync(int userId, CancellationToken cancellationToken)
    {
        UserAccount? user = await _db.UserAccounts
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        return user;
    }

    /// <inheritdoc/>
    public async Task<(List<UserAccount> Users, Dictionary<int, List<UserTenantRole>> RolesByUser)> ListUsersForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        List<int> tenantUserIds = await _db.UserTenantRoles
            .Where(r => r.AssignedTenantId == tenantId && r.IsActive)
            .Select(r => r.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        List<UserAccount> users = await _db.UserAccounts
            .Where(u => u.IsSystem == false && tenantUserIds.Contains(u.Id))
            .OrderBy(u => u.Username)
            .ToListAsync(cancellationToken);

        List<UserTenantRole> tenantRoles = await _db.UserTenantRoles
            .LoadWith(r => r.AssignedTenant)
            .Where(r => r.IsActive && r.AssignedTenantId == tenantId)
            .ToListAsync(cancellationToken);

        Dictionary<int, List<UserTenantRole>> rolesByUser = tenantRoles
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return (users, rolesByUser);
    }

    /// <inheritdoc/>
    public async Task<(UserAccount? User, List<UserTenantRole> Roles)> GetUserDetailForTenantAsync(int userId, int tenantId, CancellationToken cancellationToken)
    {
        bool userInTenant = await _db.UserTenantRoles
            .AnyAsync(r => r.UserId == userId && r.AssignedTenantId == tenantId && r.IsActive, cancellationToken);

        if (userInTenant == false)
        {
            return (null, []);
        }

        UserAccount? user = await _db.UserAccounts
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return (null, []);
        }

        List<UserTenantRole> roles = await _db.UserTenantRoles
            .LoadWith(r => r.AssignedTenant)
            .Where(r => r.UserId == userId && r.AssignedTenantId == tenantId && r.IsActive)
            .ToListAsync(cancellationToken);

        return (user, roles);
    }

    /// <inheritdoc/>
    public async Task<int> DeactivateUserTenantRolesAsync(int targetUserId, int tenantId, int currentUserId, CancellationToken cancellationToken)
    {
        int roleUpdated = await _db.UserTenantRoles
            .Where(r => r.UserId == targetUserId &&
                         r.AssignedTenantId == tenantId &&
                         r.IsActive)
            .Set(r => r.IsActive, false)
            .Set(r => r.DisabledByUserId, currentUserId)
            .Set(r => r.DisabledAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);

        return roleUpdated;
    }

    /// <inheritdoc/>
    public async Task<bool> HasActiveRolesAsync(int userId, CancellationToken cancellationToken)
    {
        bool hasActiveRoles = await _db.UserTenantRoles
            .AnyAsync(r => r.UserId == userId && r.IsActive, cancellationToken);

        return hasActiveRoles;
    }

    /// <inheritdoc/>
    public async Task DeactivateUserAccountAsync(int userId, int currentUserId, CancellationToken cancellationToken)
    {
        await _db.UserAccounts
            .Where(u => u.Id == userId && u.IsActive && u.IsSystem == false)
            .Set(u => u.IsActive, false)
            .Set(u => u.DeletedOn, DateTimeOffset.UtcNow)
            .Set(u => u.DeletedByUserId, currentUserId)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<(List<UserAccount> Users, Dictionary<int, List<UserTenantRole>> RolesByUser)> GetAllUsersWithRolesAsync(CancellationToken cancellationToken)
    {
        List<UserAccount> users = await _db.UserAccounts
            .OrderBy(u => u.Username)
            .ToListAsync(cancellationToken);

        List<UserTenantRole> allRoles = await _db.UserTenantRoles
            .LoadWith(r => r.AssignedTenant)
            .Where(r => r.IsActive)
            .ToListAsync(cancellationToken);

        Dictionary<int, List<UserTenantRole>> rolesByUser = allRoles
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return (users, rolesByUser);
    }

    /// <inheritdoc/>
    public async Task<(List<UserAccount> Users, int TotalCount)> QueryUsersAsync(string? search, int skip, int take, CancellationToken cancellationToken)
    {
        IQueryable<UserAccount> query = _db.UserAccounts;

        if (string.IsNullOrWhiteSpace(search) == false)
        {
            string searchTrimmed = search.Trim();
            query = query.Where(u => u.Username.Contains(searchTrimmed));
        }

        int totalCount = await query.CountAsync(cancellationToken);

        List<UserAccount> users = await query
            .OrderBy(u => u.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return (users, totalCount);
    }

    /// <inheritdoc/>
    public async Task<List<UserAccount>> GetUsersByIdsAsync(List<int> userIds, CancellationToken cancellationToken)
    {
        List<UserAccount> users = await _db.UserAccounts
            .Where(u => userIds.Contains(u.Id))
            .ToListAsync(cancellationToken);

        return users;
    }

    /// <inheritdoc/>
    public async Task<(List<UserAccount> Users, int TotalCount)> SearchUsersPagedAsync(string? search, int skip, int take, CancellationToken cancellationToken)
    {
        IQueryable<UserAccount> query = _db.UserAccounts;

        if (string.IsNullOrWhiteSpace(search) == false)
        {
            string searchTrimmed = search.Trim();
            query = query.Where(u => u.Username.Contains(searchTrimmed));
        }

        int totalCount = await query.CountAsync(cancellationToken);

        List<UserAccount> users = await query
            .OrderBy(u => u.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return (users, totalCount);
    }

    /// <inheritdoc/>
    public async Task<UserAccount?> GetUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        UserAccount? user = await _db.UserAccounts
            .Where(u => (u.Username.ToLower() == email.ToLower()) && (u.IsActive == true))
            .FirstOrDefaultAsync(cancellationToken);

        return user;
    }
}
