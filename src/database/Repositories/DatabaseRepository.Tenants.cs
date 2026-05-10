// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : ITenantRepository
{
    /// <inheritdoc/>
    public async Task<IEnumerable<UserTenantRole>> GetTenantsForUserAsync(string userUniqueId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userUniqueId);

        List<UserTenantRole> roles = await (from utr in _db.UserTenantRoles
                join ua in _db.UserAccounts on utr.UserId equals ua.Id
                join t in _db.Tenants on utr.AssignedTenantId equals t.Id
                where (ua.ExternalId == userUniqueId) && t.IsActive && ua.IsActive && utr.IsActive
                select new UserTenantRole()
                {
                    AssignedAt = utr.AssignedAt,
                    AssignedTenantId = utr.AssignedTenantId,
                    AssignedTenant = t,
                    Role = utr.Role,
                    UserId = utr.UserId,
                    IsActive = utr.IsActive,
                    AssignedByUserId = utr.AssignedByUserId,
                }).ToListAsync(cancellationToken);

        return roles;
    }

    /// <inheritdoc/>
    public async Task<Tenant?> GetTenantByExternalIdAsync(string externalId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);

        Tenant? tenant = null;

        try
        {
            _logger.LogDebug("Retrieving tenant by external ID {ExternalId}", externalId);
            tenant = await _db.Tenants
                .Where(t => t.ExternalId == externalId && t.IsActive)
                .FirstOrDefaultAsync(cancellationToken);
            _logger.LogInformation("Successfully retrieved tenant by external ID {ExternalId}", externalId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while retrieving tenant by external ID {ExternalId}", externalId);
        }

        return tenant;
    }

    /// <inheritdoc/>
    public async Task<Tenant?> GetTenantByIdAsync(int tenantId, CancellationToken cancellationToken)
    {
        Tenant? tenant = null;

        try
        {
            _logger.LogDebug("Retrieving tenant by ID {TenantId}", tenantId);
            tenant = await _db.Tenants
                .Where(t => t.Id == tenantId && t.IsActive)
                .FirstOrDefaultAsync(cancellationToken);
            _logger.LogInformation("Successfully retrieved tenant by ID {TenantId}", tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while retrieving tenant by ID {TenantId}", tenantId);
        }

        return tenant;
    }

    /// <inheritdoc/>
    public async Task<Tenant?> GetTenantByNameAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Tenant? tenant = null;

        try
        {
            _logger.LogDebug("Retrieving tenant by name {TenantName}", name);
            // Tenant names are normalized to lowercase at creation time.
            tenant = await _db.Tenants
                .Where(t => t.Name == name.ToLowerInvariant() && t.IsActive)
                .FirstOrDefaultAsync(cancellationToken);
            _logger.LogInformation("Successfully retrieved tenant by name {TenantName}", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while retrieving tenant by name {TenantName}", name);
        }

        return tenant;
    }

    /// <inheritdoc/>
    public async Task<Tenant> CreateTenantAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant.Name);

        try
        {
            // Normalize tenant name to lowercase for consistent case-insensitive lookups.
            tenant.Name = tenant.Name.ToLowerInvariant();
            _logger.LogDebug("Creating new tenant with ExternalId {ExternalId}", tenant.ExternalId);
            int newTenantId = await _db.InsertWithInt32IdentityAsync(tenant, token: cancellationToken);
            tenant.Id = newTenantId;
            _logger.LogInformation("Successfully created new tenant with ExternalId {ExternalId}", tenant.ExternalId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while creating a new tenant with ExternalId {ExternalId}", tenant.ExternalId);
            throw;
        }

        return tenant;
    }

    /// <inheritdoc/>
    public async Task CreateUserTenantRoleAsync(UserTenantRole role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        try
        {
            _logger.LogDebug("Creating UserTenantRole for user {UserId} in tenant {TenantId}", role.UserId, role.AssignedTenantId);
            await _db.InsertAsync(role, token: cancellationToken);
            _logger.LogInformation("Created UserTenantRole for user {UserId} in tenant {TenantId} with role {Role}", role.UserId, role.AssignedTenantId, role.Role);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create UserTenantRole for user {UserId} in tenant {TenantId}", role.UserId, role.AssignedTenantId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<TenantOidcConfiguration?> GetTenantOidcConfigurationAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        TenantOidcConfiguration? config = null;

        try
        {
            _logger.LogDebug("Retrieving OIDC configuration for tenant {TenantId}", tenantId);
            config = await _db.TenantOidcConfigurations
                .Where(c => c.TenantId == tenantId && c.IsEnabled)
                .FirstOrDefaultAsync(cancellationToken);
            _logger.LogInformation("Retrieved OIDC configuration for tenant {TenantId}: {Found}", tenantId, config is not null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve OIDC configuration for tenant {TenantId}", tenantId);
        }

        return config;
    }

    /// <inheritdoc/>
    public async Task<List<Tenant>> ListAllTenantsAsync(CancellationToken cancellationToken)
    {
        List<Tenant> tenants = await _db.Tenants
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);

        return tenants;
    }

    /// <inheritdoc/>
    public async Task<List<Tenant>> ListTenantsByIdsAsync(List<int> tenantIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);

        List<Tenant> tenants = await _db.Tenants
            .Where(t => tenantIds.Contains(t.Id))
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);

        return tenants;
    }

    /// <inheritdoc/>
    public async Task<TenantOidcConfiguration?> GetTenantOidcConfigByTenantIdAsync(int tenantId, CancellationToken cancellationToken)
    {
        TenantOidcConfiguration? config = await _db.TenantOidcConfigurations
            .Where(c => c.TenantId == tenantId)
            .FirstOrDefaultAsync(cancellationToken);

        return config;
    }

    /// <inheritdoc/>
    public async Task InsertTenantOidcConfigAsync(TenantOidcConfiguration config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        await _db.InsertAsync(config, token: cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateTenantOidcConfigAsync(int tenantId, string authority, string clientId, string clientSecret, string? metadataAddress, string emailDomain, bool isEnabled, CancellationToken cancellationToken)
    {
        await _db.TenantOidcConfigurations
            .Where(c => c.TenantId == tenantId)
            .Set(c => c.Authority, authority)
            .Set(c => c.ClientId, clientId)
            .Set(c => c.ClientSecret, clientSecret)
            .Set(c => c.MetadataAddress, metadataAddress)
            .Set(c => c.EmailDomain, emailDomain)
            .Set(c => c.IsEnabled, isEnabled)
            .Set(c => c.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> DisableTenantOidcConfigAsync(int tenantId, CancellationToken cancellationToken)
    {
        int updated = await _db.TenantOidcConfigurations
            .Where(c => c.TenantId == tenantId && c.IsEnabled == true)
            .Set(c => c.IsEnabled, false)
            .Set(c => c.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);

        return updated;
    }

    /// <inheritdoc/>
    public async Task<bool> HasNonOidcTenantAdminAsync(int tenantId, CancellationToken cancellationToken)
    {
        bool hasNonOidcAdmin = await (
            from utr in _db.UserTenantRoles
            join u in _db.UserAccounts on utr.UserId equals u.Id
            where utr.AssignedTenantId == tenantId &&
                  utr.Role == Enums.UserAccountRoles.TenantAdmin &&
                  utr.IsActive == true &&
                  u.IsActive == true &&
                  u.AuthProvider != Enums.AuthProviderType.CustomOidc
            select u.Id
        ).AnyAsync(cancellationToken);

        return hasNonOidcAdmin;
    }

    /// <inheritdoc/>
    public async Task<TenantOidcConfiguration?> GetTenantOidcConfigurationByEmailDomainAsync(string emailDomain, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(emailDomain);

        TenantOidcConfiguration? config = null;

        try
        {
            _logger.LogDebug("Retrieving OIDC configuration for email domain {EmailDomain}", emailDomain);
            // Email domains are normalized to lowercase at storage time.
            config = await _db.TenantOidcConfigurations
                .Where(c => c.EmailDomain == emailDomain.ToLowerInvariant() && c.IsEnabled)
                .FirstOrDefaultAsync(cancellationToken);
            _logger.LogInformation("Retrieved OIDC configuration for email domain {EmailDomain}: {Found}", emailDomain, config is not null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve OIDC configuration for email domain {EmailDomain}", emailDomain);
        }

        return config;
    }

    /// <inheritdoc/>
    public async Task<(List<Tenant> Tenants, int TotalCount)> QueryTenantsAsync(string? search, int skip, int take, CancellationToken cancellationToken)
    {
        IQueryable<Tenant> query = _db.Tenants;

        if (string.IsNullOrWhiteSpace(search) == false)
        {
            string searchTrimmed = search.Trim();
            query = query.Where(t => t.Name.Contains(searchTrimmed));
        }

        int totalCount = await query.CountAsync(cancellationToken);

        List<Tenant> tenants = await query
            .OrderBy(t => t.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return (tenants, totalCount);
    }

    /// <inheritdoc/>
    public async Task<List<UserTenantRole>> GetActiveRolesForTenantsAsync(List<int> tenantIds, CancellationToken cancellationToken)
    {
        List<UserTenantRole> roles = await _db.UserTenantRoles
            .Where(r => tenantIds.Contains(r.AssignedTenantId) && (r.IsActive == true))
            .ToListAsync(cancellationToken);

        return roles;
    }

    /// <inheritdoc/>
    public async Task<List<UserTenantRole>> GetActiveRolesForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        List<UserTenantRole> roles = await _db.UserTenantRoles
            .Where(r => (r.AssignedTenantId == tenantId) && (r.IsActive == true))
            .ToListAsync(cancellationToken);

        return roles;
    }

    /// <inheritdoc/>
    public async Task<List<UserTenantRole>> GetActiveRolesForUsersAsync(List<int> userIds, CancellationToken cancellationToken)
    {
        List<UserTenantRole> roles = await _db.UserTenantRoles
            .Where(r => userIds.Contains(r.UserId) && (r.IsActive == true))
            .ToListAsync(cancellationToken);

        return roles;
    }

    /// <inheritdoc/>
    public async Task<(List<Tenant> Tenants, int TotalCount)> SearchTenantsPagedAsync(string? search, int skip, int take, CancellationToken cancellationToken)
    {
        IQueryable<Tenant> query = _db.Tenants;

        if (string.IsNullOrWhiteSpace(search) == false)
        {
            string searchTrimmed = search.Trim();
            query = query.Where(t => t.Name.Contains(searchTrimmed));
        }

        int totalCount = await query.CountAsync(cancellationToken);

        List<Tenant> tenants = await query
            .OrderBy(t => t.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return (tenants, totalCount);
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<UserTenantRole>> GetMembersForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        List<UserTenantRole> roles = await (from utr in _db.UserTenantRoles
                join ua in _db.UserAccounts on utr.UserId equals ua.Id
                where (utr.AssignedTenantId == tenantId) && (utr.IsActive == true) && (ua.IsActive == true)
                select new UserTenantRole
                {
                    UserId = utr.UserId,
                    User = ua,
                    AssignedTenantId = utr.AssignedTenantId,
                    Role = utr.Role,
                    AssignedByUserId = utr.AssignedByUserId,
                    AssignedAt = utr.AssignedAt,
                    IsActive = utr.IsActive,
                }).ToListAsync(cancellationToken);

        return roles;
    }

    /// <inheritdoc/>
    public async Task<bool> DisableUserTenantRoleAsync(int userId, int tenantId, int disabledByUserId, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Disabling UserTenantRole for user {UserId} in tenant {TenantId}", userId, tenantId);
            int affected = await _db.UserTenantRoles
                .Where(utr => (utr.UserId == userId) &&
                               (utr.AssignedTenantId == tenantId) &&
                               (utr.IsActive == true))
                .Set(utr => utr.IsActive, false)
                .Set(utr => utr.DisabledByUserId, disabledByUserId)
                .Set(utr => utr.DisabledAt, DateTimeOffset.UtcNow)
                .UpdateAsync(cancellationToken);

            _logger.LogInformation("Disabled {Count} UserTenantRole(s) for user {UserId} in tenant {TenantId}", affected, userId, tenantId);

            return affected > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disable UserTenantRole for user {UserId} in tenant {TenantId}", userId, tenantId);
            throw;
        }
    }
}
