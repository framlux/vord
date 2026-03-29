// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// Handles cleanup of tier-gated resources when a tenant is downgraded.
/// </summary>
public interface IDowngradeCleanupService
{
    /// <summary>
    /// Cleans up resources that require Team tier when downgrading to Pro.
    /// Disables custom OIDC configuration and custom alert rules.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CleanupForProTierAsync(int tenantId, CancellationToken ct);

    /// <summary>
    /// Cleans up resources that require a paid tier when downgrading to Free.
    /// Disables all alert rules, OIDC configuration, and webhook endpoints.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CleanupForFreeTierAsync(int tenantId, CancellationToken ct);
}
