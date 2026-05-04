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
    Task UpsertOverrideAsync(int tenantId, int? machineLimit, int? retentionDays, int? alertRuleLimit, int? webhookLimit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the override for a specific tenant, reverting to tier defaults.
    /// </summary>
    Task RemoveOverrideAsync(int tenantId, CancellationToken cancellationToken = default);
}
