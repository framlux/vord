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

    /// <summary>Maximum alert rules for Pro tier subscriptions.</summary>
    [Range(0, int.MaxValue)]
    public int ProTierAlertRuleLimit { get; set; } = 25;

    /// <summary>Maximum webhook endpoints for Pro tier subscriptions.</summary>
    [Range(0, int.MaxValue)]
    public int ProTierWebhookLimit { get; set; } = 5;

    /// <summary>Maximum alert rules for Team tier subscriptions.</summary>
    [Range(0, int.MaxValue)]
    public int TeamTierAlertRuleLimit { get; set; } = 100;

    /// <summary>Maximum webhook endpoints for Team tier subscriptions.</summary>
    [Range(0, int.MaxValue)]
    public int TeamTierWebhookLimit { get; set; } = 25;
}
