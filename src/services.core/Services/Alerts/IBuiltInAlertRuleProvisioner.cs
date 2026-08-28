// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// Creates and enables a tenant's built-in alert rules. Both operations are idempotent, so every
/// route into an entitled state can call them without first working out what has already happened.
/// </summary>
public interface IBuiltInAlertRuleProvisioner
{
    /// <summary>
    /// Inserts whichever built-in rules the tenant is missing, enabled if the tenant is entitled and
    /// disabled otherwise. Existing rules are never modified.
    /// </summary>
    /// <param name="tenantId">The tenant to provision.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EnsureProvisionedAsync(int tenantId, CancellationToken ct = default);

    /// <summary>
    /// Enables every built-in rule the tenant holds. Called on each transition into an entitled
    /// state, because a downgrade to Free disables them and nothing else turns them back on.
    /// </summary>
    /// <param name="tenantId">The tenant whose built-in rules should be enabled.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EnableBuiltInsAsync(int tenantId, CancellationToken ct = default);
}
