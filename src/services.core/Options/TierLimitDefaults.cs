// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Default feature limits for a single subscription tier.
/// </summary>
public sealed class TierLimitDefaults
{
    /// <summary>
    /// Maximum number of machines allowed.
    /// </summary>
    public int MachineLimit { get; set; }

    /// <summary>
    /// Telemetry data retention period in days.
    /// </summary>
    public int RetentionDays { get; set; }

    /// <summary>
    /// Maximum number of alert rules allowed.
    /// </summary>
    public int AlertRuleLimit { get; set; }

    /// <summary>
    /// Maximum number of webhook endpoints allowed.
    /// </summary>
    public int WebhookLimit { get; set; }
}
