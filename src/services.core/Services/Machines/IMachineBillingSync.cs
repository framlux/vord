// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Synchronizes active machine counts to the billing provider after machine lifecycle changes.
/// All failures are logged and swallowed so billing sync never blocks the caller.
/// </summary>
public interface IMachineBillingSync
{
    /// <summary>
    /// Reports the current active machine count for the given tenant to the billing provider.
    /// Only reports for paid tiers; Free tier tenants are skipped.
    /// Billing failures are logged as warnings and swallowed — callers are not affected.
    /// </summary>
    /// <param name="tenantId">The tenant whose active machine count should be reported.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReportActiveMachineUsageAsync(int tenantId, CancellationToken ct);
}
