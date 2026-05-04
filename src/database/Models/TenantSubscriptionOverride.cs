// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Per-tenant overrides for subscription feature limits. When set, these values
/// take precedence over the tier defaults from TierFeatureLimits. Null values
/// mean "use the tier default".
/// </summary>
[Table(Name = "TenantSubscriptionOverrides")]
public sealed class TenantSubscriptionOverride
{
    /// <summary>
    /// Auto-incrementing primary key.
    /// </summary>
    [PrimaryKey, Identity]
    [Column(Name = "Id")]
    public int Id { get; set; }

    /// <summary>
    /// The tenant this override applies to.
    /// </summary>
    [Column(Name = "TenantId"), NotNull]
    public int TenantId { get; set; }

    /// <summary>
    /// Custom machine limit for this tenant. Null means use tier default.
    /// </summary>
    [Column(Name = "MachineLimit"), Nullable]
    public int? MachineLimit { get; set; }

    /// <summary>
    /// Custom retention days for this tenant. Null means use tier default.
    /// </summary>
    [Column(Name = "RetentionDays"), Nullable]
    public int? RetentionDays { get; set; }

    /// <summary>
    /// Custom alert rule limit for this tenant. Null means use tier default.
    /// </summary>
    [Column(Name = "AlertRuleLimit"), Nullable]
    public int? AlertRuleLimit { get; set; }

    /// <summary>
    /// Custom webhook limit for this tenant. Null means use tier default.
    /// </summary>
    [Column(Name = "WebhookLimit"), Nullable]
    public int? WebhookLimit { get; set; }

    /// <summary>
    /// When this override was created.
    /// </summary>
    [Column(Name = "CreatedAt"), NotNull]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When this override was last updated.
    /// </summary>
    [Column(Name = "UpdatedAt"), NotNull]
    public DateTimeOffset UpdatedAt { get; set; }
}
