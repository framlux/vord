// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.ComponentModel.DataAnnotations;

namespace Framlux.FleetManagement.Server.Options;

/// <summary>
/// Configuration options for subscription tier limits.
/// </summary>
public sealed class SubscriptionOptions
{
    /// <summary>
    /// Maximum number of machines allowed on the Free tier.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int FreeTierMachineLimit { get; set; } = 3;

    /// <summary>
    /// Telemetry retention period in days for the Free tier.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int FreeTierRetentionDays { get; set; } = 1;
}
