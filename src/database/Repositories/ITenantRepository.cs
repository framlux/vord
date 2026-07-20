// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for tenant, tenant roles, and tenant OIDC operations.
/// </summary>
public interface ITenantRepository
{
    /// <summary>
    /// Get the tenants and roles for the specified user.
    /// </summary>
    Task<IEnumerable<UserTenantRole>> GetTenantsForUserAsync(string userUniqueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the tenants and roles for the specified user by their account identifier.
    /// Resolving on the user-account primary key avoids the cross-provider role leak that an
    /// external-id lookup can produce when two accounts share an external id across providers.
    /// </summary>
    Task<IEnumerable<UserTenantRole>> GetTenantsForUserByIdAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a tenant by its external ID.
    /// </summary>
    Task<Tenant?> GetTenantByExternalIdAsync(string externalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a tenant by its unique identifier.
    /// </summary>
    Task<Tenant?> GetTenantByIdAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a tenant by its name.
    /// </summary>
    Task<Tenant?> GetTenantByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new tenant in the database.
    /// </summary>
    Task<Tenant> CreateTenantAsync(Tenant tenant, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a user-tenant role assignment in the database.
    /// </summary>
    Task CreateUserTenantRoleAsync(UserTenantRole role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a <see cref="UserTenantRole"/> within a serializable transaction that first re-counts
    /// active members, rejecting the insert if the tenant is already at its member limit. Returns
    /// true if the role was inserted, false if the limit was reached. A null <paramref name="memberLimit"/>
    /// means no limit is enforced.
    /// </summary>
    /// <param name="role">The role assignment to insert.</param>
    /// <param name="memberLimit">The maximum number of active members allowed, or null for no limit.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<bool> CreateUserTenantRoleWithMemberLimitAsync(UserTenantRole role, int? memberLimit, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the OIDC configuration for a tenant.
    /// </summary>
    Task<TenantOidcConfiguration?> GetTenantOidcConfigurationAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the OIDC configuration for a tenant by email domain.
    /// </summary>
    Task<TenantOidcConfiguration?> GetTenantOidcConfigurationByEmailDomainAsync(string emailDomain, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all active user-tenant roles for a specific tenant.
    /// </summary>
    Task<IEnumerable<UserTenantRole>> GetMembersForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables a user-tenant role assignment (removes a member from a tenant).
    /// </summary>
    Task<bool> DisableUserTenantRoleAsync(int userId, int tenantId, int disabledByUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the user's current active role in the tenant, or <c>null</c> when the user has no active
    /// membership there. Used to decide whether a member mutation touches a TenantAdmin before the
    /// last-administrator guard is worth evaluating.
    /// </summary>
    Task<UserAccountRoles?> GetActiveUserRoleAsync(int userId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all tenants ordered by name.
    /// </summary>
    Task<List<Tenant>> ListAllTenantsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists tenants matching the specified IDs, ordered by name.
    /// </summary>
    Task<List<Tenant>> ListTenantsByIdsAsync(List<int> tenantIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the OIDC configuration for a tenant by tenant ID regardless of enabled state.
    /// Used for admin CRUD operations where we need to read/update disabled configs.
    /// </summary>
    Task<TenantOidcConfiguration?> GetTenantOidcConfigByTenantIdAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new tenant OIDC configuration.
    /// </summary>
    Task InsertTenantOidcConfigAsync(TenantOidcConfiguration config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing tenant OIDC configuration.
    /// </summary>
    Task UpdateTenantOidcConfigAsync(int tenantId, string authority, string clientId, string clientSecret, string? metadataAddress, string emailDomain, bool isEnabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables OIDC configuration for a tenant by setting IsEnabled to false.
    /// Returns the number of rows updated.
    /// </summary>
    Task<int> DisableTenantOidcConfigAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every <see cref="TenantOidcConfiguration"/> row regardless of enabled state.
    /// Used by maintenance jobs (e.g., legacy-secret encryption migration). Returns only the
    /// fields needed to read and migrate <c>ClientSecret</c>.
    /// </summary>
    Task<IReadOnlyList<TenantOidcConfiguration>> ListAllTenantOidcConfigsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates only the <c>ClientSecret</c> column of a tenant's OIDC configuration.
    /// Returns the number of rows updated. Used by the legacy-secret migration to re-protect
    /// existing rows without rewriting the entire configuration.
    /// </summary>
    Task<int> UpdateTenantOidcClientSecretAsync(int tenantId, string clientSecret, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a tenant has at least one active TenantAdmin who does not use CustomOidc
    /// as their authentication provider. Used to guard Team-to-lower-tier downgrades.
    /// </summary>
    /// <param name="tenantId">The tenant to check.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<bool> HasNonOidcTenantAdminAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns active tenant role records for the specified tenant IDs.
    /// </summary>
    /// <param name="tenantIds">The tenant IDs to query.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<UserTenantRole>> GetActiveRolesForTenantsAsync(List<int> tenantIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns active user-tenant roles for the specified user IDs.
    /// </summary>
    /// <param name="userIds">The user IDs to query.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<UserTenantRole>> GetActiveRolesForUsersAsync(List<int> userIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the email addresses (usernames) of all active TenantAdmin users for a tenant.
    /// Used to resolve alert email recipients.
    /// </summary>
    /// <param name="tenantId">The tenant to query.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<string>> GetTenantAdminEmailsAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of tenants with optional search, ordered by ID.
    /// </summary>
    /// <param name="search">Optional name search term.</param>
    /// <param name="skip">Number of records to skip.</param>
    /// <param name="take">Number of records to take.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<(List<Tenant> Tenants, int TotalCount)> SearchTenantsPagedAsync(string? search, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the active members (active UserTenantRole rows, including the owner/admin) for a tenant.
    /// Counts active tenant-role memberships (seat occupancy); intentionally does not join UserAccount.IsActive,
    /// to stay consistent with the serializable member-limit guard.
    /// </summary>
    /// <param name="tenantId">The tenant to count members for.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<int> CountActiveMembersAsync(int tenantId, CancellationToken cancellationToken = default);
}
