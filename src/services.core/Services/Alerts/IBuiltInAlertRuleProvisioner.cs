// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

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
    /// Restores whatever a downgrade sweep disabled, for a tenant that has just been written to
    /// <paramref name="currentTier"/> and <paramref name="currentStatus"/>. Safe to call from every
    /// route into a paid tier — checkout, payment recovery, drift correction, administrative grant —
    /// because the decision about what a given transition may restore is taken here rather than at
    /// the call site.
    /// </summary>
    /// <param name="tenantId">The tenant whose rules should be restored.</param>
    /// <param name="priorSubscription">
    /// The subscription row as it stood before the transition was written, or <c>null</c> if the
    /// tenant had none. This is the only thing that distinguishes a return from a swept state from
    /// an ordinary renewal, so it must be read before the write that overwrites it.
    /// </param>
    /// <param name="currentTier">The tier the tenant now holds.</param>
    /// <param name="currentStatus">The status the tenant now holds.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RestoreForTierAsync(
        int tenantId,
        TenantSubscription? priorSubscription,
        SubscriptionTier currentTier,
        SubscriptionStatus currentStatus,
        CancellationToken ct = default);
}
