// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents an alert rule that defines conditions for triggering alerts.
/// </summary>
[Table(Name = TableNames.AlertRules)]
public sealed class AlertRule
{
    /// <summary>The unique identifier.</summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public int Id { get; set; }

    /// <summary>The tenant this rule belongs to.</summary>
    [Column("TenantId"), NotNull]
    public int TenantId { get; set; }

    /// <summary>The associated tenant.</summary>
    [Association(ThisKey = nameof(TenantId), OtherKey = nameof(Tenant.Id))]
    public Tenant? Tenant { get; set; }

    /// <summary>The rule name.</summary>
    [Column("Name"), NotNull]
    public required string Name { get; set; }

    /// <summary>Optional description.</summary>
    [Column("Description"), Nullable]
    public string? Description { get; set; }

    /// <summary>The metric to evaluate.</summary>
    [Column("Metric"), NotNull]
    public required AlertMetric Metric { get; set; }

    /// <summary>The comparison operator.</summary>
    [Column("Operator"), NotNull]
    public required AlertOperator Operator { get; set; }

    /// <summary>The threshold value.</summary>
    [Column("Threshold"), NotNull]
    public required decimal Threshold { get; set; }

    /// <summary>How long the condition must persist (in minutes) before firing.</summary>
    [Column("DurationMinutes"), NotNull]
    public int DurationMinutes { get; set; }

    /// <summary>The severity level.</summary>
    [Column("Severity"), NotNull]
    public required AlertSeverity Severity { get; set; }

    /// <summary>Whether the rule is enabled.</summary>
    [Column("IsEnabled"), NotNull]
    public bool IsEnabled { get; set; }

    /// <summary>Whether to send email notifications.</summary>
    [Column("NotifyEmail"), NotNull]
    public bool NotifyEmail { get; set; }

    /// <summary>Whether to send webhook notifications.</summary>
    [Column("NotifyWebhook"), NotNull]
    public bool NotifyWebhook { get; set; }

    /// <summary>Whether this is a custom rule (true) or a system default (false).</summary>
    [Column("IsCustom"), NotNull]
    public bool IsCustom { get; set; }

    /// <summary>The user who created this rule.</summary>
    [Column("CreatedByUserId"), NotNull]
    public int CreatedByUserId { get; set; }

    /// <summary>When the rule was created.</summary>
    [Column("CreatedAt"), NotNull]
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the rule was last updated.</summary>
    [Column("UpdatedAt"), NotNull]
    public required DateTimeOffset UpdatedAt { get; set; }
}
