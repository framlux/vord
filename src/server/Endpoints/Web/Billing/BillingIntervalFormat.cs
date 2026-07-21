// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.Vord.BillingGrpc;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// Maps the proto billing interval to the wire string used by billing DTOs.
/// </summary>
internal static class BillingIntervalFormat
{
    /// <summary>
    /// Returns "monthly" or "annual" for known intervals, or null when the interval is None or unknown.
    /// </summary>
    internal static string? ToWireString(BillingInterval interval)
    {
        return interval switch
        {
            BillingInterval.Monthly => "monthly",
            BillingInterval.Annual => "annual",
            _ => null,
        };
    }
}
