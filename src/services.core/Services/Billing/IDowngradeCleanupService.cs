// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Handles cleanup of tier-gated resources when a tenant is downgraded.
/// </summary>
/// <remarks>
/// Both cleanups write on the caller's connection, so they belong inside the transaction that changes
/// the tenant's tier, before its commit. The tier write holds the subscription row's lock until then,
/// which makes an enterprise agreement being applied to the same tenant wait for the cleanup instead
/// of landing between a committed tier change and a cleanup that would then run on an Enterprise
/// tenant. Neither cleanup touches machines: a tenant left over its machine limit keeps every machine,
/// and registration refuses new ones until it is under the limit again.
/// </remarks>
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
    /// Disables all alert rules, OIDC configuration, and webhook endpoints. Machines are not touched.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CleanupForFreeTierAsync(int tenantId, CancellationToken ct);
}
