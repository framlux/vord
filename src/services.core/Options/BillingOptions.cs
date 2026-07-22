// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Configuration options for Stripe billing integration.
/// </summary>
public sealed class BillingOptions
{
    /// <summary>
    /// Whether billing is enabled for this deployment.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The gRPC URL for the billing API service.
    /// </summary>
    public string GrpcUrl { get; set; } = string.Empty;

}
