// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Validates whether a downgrade from Team tier is safe by ensuring at least one
/// TenantAdmin can still log in after custom OIDC is disabled.
/// </summary>
public interface IDowngradeGuardService
{
    /// <summary>
    /// Checks whether a downgrade from Team tier is safe for the specified tenant.
    /// Returns true if at least one active TenantAdmin uses a social login provider.
    /// </summary>
    /// <param name="tenantId">The tenant ID to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the downgrade is safe; otherwise, false.</returns>
    Task<bool> CanDowngradeFromTeamAsync(int tenantId, CancellationToken ct);
}
