// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Represents the effective feature limits for a tenant after applying overrides.
/// </summary>
public sealed record EffectiveLimits
{
    /// <summary>Maximum machines allowed.</summary>
    public int MachineLimit { get; init; }

    /// <summary>Data retention in days.</summary>
    public int RetentionDays { get; init; }

    /// <summary>Maximum alert rules allowed.</summary>
    public int AlertRuleLimit { get; init; }

    /// <summary>Maximum webhooks allowed.</summary>
    public int WebhookLimit { get; init; }
}
