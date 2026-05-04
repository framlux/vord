// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Defines the feature limits for a subscription tier. Replaces the hardcoded
/// SubscriptionOptions configuration with a database-managed table that can
/// be updated via the admin panel without redeployment.
/// </summary>
[Table(Name = "TierFeatureLimits")]
public sealed class TierFeatureLimit
{
    /// <summary>
    /// Auto-incrementing primary key.
    /// </summary>
    [PrimaryKey, Identity]
    [Column(Name = "Id")]
    public int Id { get; set; }

    /// <summary>
    /// The subscription tier these limits apply to.
    /// </summary>
    [Column(Name = "Tier"), NotNull]
    public SubscriptionTier Tier { get; set; }

    /// <summary>
    /// Maximum number of machines allowed.
    /// </summary>
    [Column(Name = "MachineLimit"), NotNull]
    public required int MachineLimit { get; set; }

    /// <summary>
    /// Telemetry data retention period in days.
    /// </summary>
    [Column(Name = "RetentionDays"), NotNull]
    public required int RetentionDays { get; set; }

    /// <summary>
    /// Maximum number of alert rules allowed.
    /// </summary>
    [Column(Name = "AlertRuleLimit"), NotNull]
    public required int AlertRuleLimit { get; set; }

    /// <summary>
    /// Maximum number of webhook endpoints allowed.
    /// </summary>
    [Column(Name = "WebhookLimit"), NotNull]
    public required int WebhookLimit { get; set; }

    /// <summary>
    /// When these limits were last updated.
    /// </summary>
    [Column(Name = "UpdatedAt"), NotNull]
    public DateTimeOffset UpdatedAt { get; set; }
}
