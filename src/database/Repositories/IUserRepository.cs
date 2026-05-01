// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for user account operations.
/// </summary>
public interface IUserRepository
{
    /// <summary>
    /// Get a user by their external ID.
    /// </summary>
    Task<UserAccount?> GetUserByExternalIdAsync(string externalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new user account in the database.
    /// </summary>
    Task<UserAccount> CreateUserAccountAsync(UserAccount user, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a user's email address in the database.
    /// </summary>
    Task UpdateUserEmailAsync(int userId, string newEmail, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a user's authentication provider in the database.
    /// </summary>
    Task UpdateUserAuthProviderAsync(int userId, AuthProviderType authProvider, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines if any user accounts exist in the database.
    /// </summary>
    Task<bool> DoAnyUsersExistAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a user account by email address.
    /// </summary>
    Task<UserAccount?> GetUserByEmailAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a user account by its primary key.
    /// </summary>
    Task<UserAccount?> GetUserByIdAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists non-system user accounts that have active roles in the specified tenant, ordered by username.
    /// Includes their active tenant roles with loaded tenant navigation properties.
    /// </summary>
    Task<(List<UserAccount> Users, Dictionary<int, List<UserTenantRole>> RolesByUser)> ListUsersForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a user and their active roles for a specific tenant. Returns null if the user has no active role in that tenant.
    /// </summary>
    Task<(UserAccount? User, List<UserTenantRole> Roles)> GetUserDetailForTenantAsync(int userId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deactivates all active roles for a user in a specific tenant. Returns the number of roles updated.
    /// </summary>
    Task<int> DeactivateUserTenantRolesAsync(int targetUserId, int tenantId, int currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether the user has any active roles in any tenant.
    /// </summary>
    Task<bool> HasActiveRolesAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deactivates a user account by marking it as inactive and recording who deleted it.
    /// Only applies to non-system, currently active accounts.
    /// </summary>
    Task DeactivateUserAccountAsync(int userId, int currentUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all user accounts with their active tenant roles, ordered by username.
    /// Used for admin panel views.
    /// </summary>
    Task<(List<UserAccount> Users, Dictionary<int, List<UserTenantRole>> RolesByUser)> GetAllUsersWithRolesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of user accounts with optional search filter, ordered by ID.
    /// </summary>
    /// <param name="search">Optional username search term.</param>
    /// <param name="skip">Number of records to skip.</param>
    /// <param name="take">Number of records to take.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<(List<UserAccount> Users, int TotalCount)> QueryUsersAsync(string? search, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns user accounts for the specified user IDs.
    /// </summary>
    /// <param name="userIds">The user IDs to retrieve.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<UserAccount>> GetUsersByIdsAsync(List<int> userIds, CancellationToken cancellationToken = default);
}
