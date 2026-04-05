// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Database.Cache;

/// <inheritdoc/>
public partial class DatabaseCache : IDatabaseCache
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
    public async Task<TenantSubscription> CreateTenantSubscriptionAsync(TenantSubscription subscription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        try
        {
            _logger.LogDebug("Creating subscription for tenant {TenantId}", subscription.TenantId);
            int newId = await _db.InsertWithInt32IdentityAsync(subscription, token: cancellationToken);
            subscription.Id = newId;
            _logger.LogInformation("Successfully created subscription {SubscriptionId} for tenant {TenantId}", newId, subscription.TenantId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create subscription for tenant {TenantId}", subscription.TenantId);
            throw;
        }

        return subscription;
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
}
