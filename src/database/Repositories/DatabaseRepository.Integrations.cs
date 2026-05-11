// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Linq;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IIntegrationRepository
{
    /// <inheritdoc/>
    public async Task<List<IntegrationEndpoint>> GetIntegrationsForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        List<IntegrationEndpoint> integrations = await _db.IntegrationEndpoints
            .Where(i => (i.TenantId == tenantId) && (i.DeletedAt == null))
            .OrderBy(i => i.Name)
            .ToListAsync(cancellationToken);

        return integrations;
    }

    /// <inheritdoc/>
    public async Task<IntegrationEndpoint> CreateIntegrationAsync(IntegrationEndpoint integration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integration);

        integration.Id = await _db.InsertWithInt32IdentityAsync(integration, token: cancellationToken);

        _logger.LogDebug("Created integration endpoint {IntegrationId} for tenant {TenantId}", integration.Id, integration.TenantId);

        return integration;
    }

    /// <inheritdoc/>
    public async Task<IntegrationEndpoint?> GetIntegrationByIdAsync(int integrationId, int tenantId, CancellationToken cancellationToken)
    {
        IntegrationEndpoint? integration = await _db.IntegrationEndpoints
            .FirstOrDefaultAsync(i => (i.Id == integrationId) && (i.TenantId == tenantId) && (i.DeletedAt == null), cancellationToken);

        return integration;
    }

    /// <inheritdoc/>
    public async Task<int> SoftDeleteIntegrationAsync(int integrationId, int tenantId, int deletedByUserId, CancellationToken cancellationToken)
    {
        int updated = await _db.IntegrationEndpoints
            .Where(i => (i.Id == integrationId) && (i.TenantId == tenantId) && (i.DeletedAt == null))
            .Set(i => i.DeletedAt, DateTimeOffset.UtcNow)
            .Set(i => i.DeletedByUserId, deletedByUserId)
            .UpdateAsync(cancellationToken);

        if (updated > 0)
        {
            _logger.LogInformation("Soft-deleted integration endpoint {IntegrationId} by user {UserId}", integrationId, deletedByUserId);
        }

        return updated;
    }

    /// <inheritdoc/>
    public async Task UpdateIntegrationEnabledAsync(int integrationId, bool isEnabled, CancellationToken cancellationToken)
    {
        await _db.IntegrationEndpoints
            .Where(i => (i.Id == integrationId) && (i.DeletedAt == null))
            .Set(i => i.IsEnabled, isEnabled)
            .Set(i => i.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateIntegrationNameAsync(int integrationId, string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await _db.IntegrationEndpoints
            .Where(i => (i.Id == integrationId) && (i.DeletedAt == null))
            .Set(i => i.Name, name)
            .Set(i => i.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateIntegrationConfigurationAsync(int integrationId, string configuration, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration);

        await _db.IntegrationEndpoints
            .Where(i => (i.Id == integrationId) && (i.DeletedAt == null))
            .Set(i => i.Configuration, configuration)
            .Set(i => i.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateIntegrationAsync(int integrationId, string? name, bool? isEnabled, string? configuration, CancellationToken cancellationToken)
    {
        IUpdatable<IntegrationEndpoint> query = _db.IntegrationEndpoints
            .Where(i => (i.Id == integrationId) && (i.DeletedAt == null))
            .AsUpdatable();

        if (name is not null)
        {
            query = query.Set(i => i.Name, name);
        }

        if (isEnabled is not null)
        {
            query = query.Set(i => i.IsEnabled, isEnabled.Value);
        }

        if (configuration is not null)
        {
            query = query.Set(i => i.Configuration, configuration);
        }

        query = query.Set(i => i.UpdatedAt, DateTimeOffset.UtcNow);

        await query.UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> CountIntegrationsForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        int count = await _db.IntegrationEndpoints
            .Where(i => (i.TenantId == tenantId) && (i.DeletedAt == null))
            .CountAsync(cancellationToken);

        return count;
    }

    /// <inheritdoc/>
    public async Task<int> DisableIntegrationsForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        int updated = await _db.IntegrationEndpoints
            .Where(i => (i.TenantId == tenantId) && (i.IsEnabled == true) && (i.DeletedAt == null))
            .Set(i => i.IsEnabled, false)
            .Set(i => i.UpdatedAt, DateTimeOffset.UtcNow)
            .UpdateAsync(cancellationToken);

        if (updated > 0)
        {
            _logger.LogInformation("Disabled {Count} integration endpoints for tenant {TenantId}", updated, tenantId);
        }

        return updated;
    }

    /// <inheritdoc/>
    public async Task<List<IntegrationEndpoint>> GetEnabledIntegrationsForTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        List<IntegrationEndpoint> integrations = await _db.IntegrationEndpoints
            .Where(i => (i.TenantId == tenantId) && (i.IsEnabled == true) && (i.DeletedAt == null))
            .ToListAsync(cancellationToken);

        return integrations;
    }
}
