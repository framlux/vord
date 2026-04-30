// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// Service for managing tenant subscriptions and billing.
/// </summary>
public interface ISubscriptionService
{
    /// <summary>
    /// Gets the subscription for a tenant.
    /// </summary>
    Task<TenantSubscription?> GetSubscriptionForTenantAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Checks if a tenant can approve another machine.
    /// </summary>
    Task<bool> CanApproveMachineAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Creates a Free-tier subscription for a tenant.
    /// </summary>
    Task<TenantSubscription> ProvisionFreeSubscriptionAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Gets the retention days for a tenant.
    /// </summary>
    Task<int> GetRetentionDaysForTenantAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Gets the active machine count for a tenant.
    /// </summary>
    Task<int> GetMachineCountForTenantAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Ensures a subscription exists for the given tenant.
    /// If no subscription exists, provisions a Free-tier subscription.
    /// If an inactive Free subscription exists, reactivates it.
    /// </summary>
    Task EnsureSubscriptionExistsAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Gets the machine count for a tenant at a specific point in time.
    /// Reconstructs from RegisteredOn/DeletedOn timestamps.
    /// </summary>
    Task<int> GetMachineCountAtDateAsync(int tenantId, DateTimeOffset targetDate, CancellationToken ct = default);

    /// <summary>Checks whether the tenant can create another alert rule within their subscription limit.</summary>
    /// <param name="tenantId">The tenant to check.</param>
    /// <param name="db">Optional existing database context to avoid creating a new scope.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> CanCreateAlertRuleAsync(int tenantId, DatabaseContext? db = null, CancellationToken ct = default);

    /// <summary>Checks whether the tenant can create another webhook endpoint within their subscription limit.</summary>
    /// <param name="tenantId">The tenant to check.</param>
    /// <param name="db">Optional existing database context to avoid creating a new scope.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> CanCreateWebhookAsync(int tenantId, DatabaseContext? db = null, CancellationToken ct = default);
}
