// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for managing per-tenant subscription limit overrides.
/// </summary>
public interface ITenantSubscriptionOverrideRepository
{
    /// <summary>
    /// Gets the override for a specific tenant, or null if none exists.
    /// </summary>
    Task<TenantSubscriptionOverride?> GetOverrideForTenantAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates the override for a specific tenant. Null values mean "use tier default".
    /// </summary>
    Task UpsertOverrideAsync(int tenantId, int? machineLimit, int? retentionDays, int? alertRuleLimit, int? webhookLimit, int? memberLimit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates the override for a tenant unless the tenant is on the Enterprise tier, whose
    /// limits belong to its agreement. The Enterprise test is part of the write itself rather than a
    /// separate read, so an agreement that commits between a caller's check and this write cannot be
    /// overwritten. Null values mean "use tier default".
    /// </summary>
    /// <param name="tenantId">The tenant whose override is written.</param>
    /// <param name="machineLimit">The machine limit, or null for the tier default.</param>
    /// <param name="retentionDays">The retention days, or null for the tier default.</param>
    /// <param name="alertRuleLimit">The alert-rule limit, or null for the tier default.</param>
    /// <param name="webhookLimit">The webhook limit, or null for the tier default.</param>
    /// <param name="memberLimit">The member limit, or null for the tier default.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>True when the override was written; false when the tenant is on the Enterprise tier and nothing was changed.</returns>
    Task<bool> UpsertOverrideUnlessEnterpriseAsync(int tenantId, int? machineLimit, int? retentionDays, int? alertRuleLimit, int? webhookLimit, int? memberLimit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the overrides for the given tenants in one query; tenants without an override are absent from the result.
    /// </summary>
    /// <param name="tenantIds">The tenant IDs to query.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<TenantSubscriptionOverride>> GetOverridesForTenantsAsync(List<int> tenantIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the override for a specific tenant, reverting to tier defaults.
    /// </summary>
    Task RemoveOverrideAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the override for a tenant, reverting to tier defaults, unless the tenant is on the
    /// Enterprise tier, whose limits belong to its agreement. The Enterprise test is part of the delete
    /// itself rather than a separate read, so an agreement that commits between a caller's check and
    /// this delete cannot have its limits removed.
    /// </summary>
    /// <param name="tenantId">The tenant whose override is removed.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>True when the tenant is not on the Enterprise tier (whether or not it had an override to remove); false when it is and nothing was changed.</returns>
    Task<bool> RemoveOverrideUnlessEnterpriseAsync(int tenantId, CancellationToken cancellationToken = default);
}
