// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents an alert event triggered by an alert rule.
/// </summary>
[Table(Name = TableNames.AlertEvents)]
public sealed class AlertEvent
{
    /// <summary>The unique identifier.</summary>
    [PrimaryKey, Identity]
    [Column("Id"), NotNull]
    public long Id { get; set; }

    /// <summary>The alert rule that triggered this event.</summary>
    [Column("AlertRuleId"), NotNull]
    public int AlertRuleId { get; set; }

    /// <summary>The associated alert rule.</summary>
    [Association(ThisKey = nameof(AlertRuleId), OtherKey = nameof(AlertRule.Id))]
    public AlertRule? AlertRule { get; set; }

    /// <summary>The tenant this event belongs to.</summary>
    [Column("TenantId"), NotNull]
    public int TenantId { get; set; }

    /// <summary>The machine that triggered this event.</summary>
    [Column("MachineId"), NotNull]
    public long MachineId { get; set; }

    /// <summary>The severity at the time of triggering.</summary>
    [Column("Severity"), NotNull]
    public required AlertSeverity Severity { get; set; }

    /// <summary>A human-readable message describing the event.</summary>
    [Column("Message"), NotNull]
    public required string Message { get; set; }

    /// <summary>Additional JSON details about the event.</summary>
    [Column("Details", DataType = LinqToDB.DataType.BinaryJson), Nullable]
    public string? Details { get; set; }

    /// <summary>The status of this event.</summary>
    [Column("Status"), NotNull]
    public required AlertEventStatus Status { get; set; }

    /// <summary>When the alert was triggered.</summary>
    [Column("TriggeredAt"), NotNull]
    public required DateTimeOffset TriggeredAt { get; set; }

    /// <summary>When the alert was acknowledged, if applicable.</summary>
    [Column("AcknowledgedAt"), Nullable]
    public DateTimeOffset? AcknowledgedAt { get; set; }

    /// <summary>When the alert was resolved, if applicable.</summary>
    [Column("ResolvedAt"), Nullable]
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>When this record was soft-deleted for retention cleanup.</summary>
    [Column("DeletedAt"), Nullable]
    public DateTimeOffset? DeletedAt { get; set; }
}
