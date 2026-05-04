// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Options;

/// <summary>
/// Configuration-driven default limits for each subscription tier.
/// The database TierFeatureLimits table is the live source of truth (editable via admin panel).
/// These defaults serve as the initial seed values and a resilient fallback if the database
/// lookup fails or the tier row is missing.
/// </summary>
public sealed class TierDefaultOptions
{
    /// <summary>
    /// Default limits for the Free tier.
    /// </summary>
    public TierLimitDefaults Free { get; set; } = new();

    /// <summary>
    /// Default limits for the Pro tier.
    /// </summary>
    public TierLimitDefaults Pro { get; set; } = new();

    /// <summary>
    /// Default limits for the Team tier.
    /// </summary>
    public TierLimitDefaults Team { get; set; } = new();
}

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
