// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for integration endpoint operations.
/// </summary>
public interface IIntegrationRepository
{
    /// <summary>
    /// Returns all non-deleted integration endpoints for a tenant, ordered by name.
    /// </summary>
    Task<List<IntegrationEndpoint>> GetIntegrationsForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new integration endpoint and sets its generated ID.
    /// </summary>
    Task<IntegrationEndpoint> CreateIntegrationAsync(IntegrationEndpoint integration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns an integration endpoint by ID scoped to a tenant (excludes deleted), or null if not found.
    /// </summary>
    Task<IntegrationEndpoint?> GetIntegrationByIdAsync(int integrationId, int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes an integration endpoint by setting DeletedAt and DeletedByUserId.
    /// Scoped to the specified tenant for safety.
    /// </summary>
    Task<int> SoftDeleteIntegrationAsync(int integrationId, int tenantId, int deletedByUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the enabled flag on an integration endpoint.
    /// </summary>
    Task UpdateIntegrationEnabledAsync(int integrationId, bool isEnabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the name of an integration endpoint.
    /// </summary>
    Task UpdateIntegrationNameAsync(int integrationId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the configuration JSON of an integration endpoint.
    /// </summary>
    Task UpdateIntegrationConfigurationAsync(int integrationId, string configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates multiple fields on an integration endpoint in a single query.
    /// Only non-null parameters are applied. Sets UpdatedAt automatically.
    /// </summary>
    Task UpdateIntegrationAsync(int integrationId, string? name, bool? isEnabled, string? configuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the count of non-deleted integration endpoints for a tenant.
    /// </summary>
    Task<int> CountIntegrationsForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disables all non-deleted integration endpoints for a tenant. Returns the number of rows updated.
    /// </summary>
    Task<int> DisableIntegrationsForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all enabled, non-deleted integration endpoints for a tenant.
    /// </summary>
    Task<List<IntegrationEndpoint>> GetEnabledIntegrationsForTenantAsync(int tenantId, CancellationToken cancellationToken = default);
}
