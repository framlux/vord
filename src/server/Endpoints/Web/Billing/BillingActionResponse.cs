// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// Shared response shape for the billing action endpoints (cancel, resume, reactivate, downgrade).
/// </summary>
public sealed class BillingActionResponse
{
    /// <summary>Whether the requested billing action was carried out successfully.</summary>
    public bool Success { get; set; }

    /// <summary>Message describing the result.</summary>
    public string Message { get; set; } = string.Empty;
}
