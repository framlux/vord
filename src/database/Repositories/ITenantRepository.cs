// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

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
    /// Checks whether a tenant has at least one active TenantAdmin who does not use CustomOidc
    /// as their authentication provider. Used to guard Team-to-lower-tier downgrades.
    /// </summary>
    /// <param name="tenantId">The tenant to check.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<bool> HasNonOidcTenantAdminAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of tenants with optional search filter, ordered by ID.
    /// </summary>
    /// <param name="search">Optional name search term.</param>
    /// <param name="skip">Number of records to skip.</param>
    /// <param name="take">Number of records to take.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<(List<Tenant> Tenants, int TotalCount)> QueryTenantsAsync(string? search, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns active tenant role records for the specified tenant IDs.
    /// </summary>
    /// <param name="tenantIds">The tenant IDs to query.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<UserTenantRole>> GetActiveRolesForTenantsAsync(List<int> tenantIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns active tenant role records for a specific tenant.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<UserTenantRole>> GetActiveRolesForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns active user-tenant roles for the specified user IDs.
    /// </summary>
    /// <param name="userIds">The user IDs to query.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<UserTenantRole>> GetActiveRolesForUsersAsync(List<int> userIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paginated list of tenants with optional search, ordered by ID.
    /// </summary>
    /// <param name="search">Optional name search term.</param>
    /// <param name="skip">Number of records to skip.</param>
    /// <param name="take">Number of records to take.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<(List<Tenant> Tenants, int TotalCount)> SearchTenantsPagedAsync(string? search, int skip, int take, CancellationToken cancellationToken = default);
}
