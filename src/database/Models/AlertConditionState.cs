// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Tracks the timestamp at which an alert rule's condition first became true for a given
/// machine. Used by AlertEvaluationJob to enforce the rule's DurationMinutes window.
/// Rows are deleted when the condition clears, when the alert fires, or when the rule
/// is deleted. Replaces the previous Redis key <c>alert:condition:{ruleId}:{machineId}</c>.
/// </summary>
[Table(TableNames.AlertConditionStates)]
public sealed class AlertConditionState
{
    /// <summary>Primary key.</summary>
    [PrimaryKey, Identity]
    [Column("Id")]
    public long Id { get; set; }

    /// <summary>The alert rule this state row belongs to.</summary>
    [Column("AlertRuleId"), NotNull]
    public int AlertRuleId { get; set; }

    /// <summary>The machine the condition was observed on.</summary>
    [Column("MachineId"), NotNull]
    public long MachineId { get; set; }

    /// <summary>UTC timestamp when the condition first became true for this rule+machine.</summary>
    [Column("FirstTriggeredAt"), NotNull]
    public DateTimeOffset FirstTriggeredAt { get; set; }

    /// <summary>UTC timestamp of the last evaluation pass that observed the condition still true.</summary>
    [Column("LastObservedAt"), NotNull]
    public DateTimeOffset LastObservedAt { get; set; }
}
