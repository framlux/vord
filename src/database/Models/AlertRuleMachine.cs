// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Represents the association between an alert rule and a specific machine,
/// allowing alert rules to be scoped to individual machines.
/// </summary>
[Table(Name = TableNames.AlertRuleMachines)]
public sealed class AlertRuleMachine
{
    /// <summary>The alert rule identifier.</summary>
    [PrimaryKey(1)]
    [Column("AlertRuleId"), NotNull]
    public int AlertRuleId { get; set; }

    /// <summary>The machine identifier.</summary>
    [PrimaryKey(2)]
    [Column("MachineId"), NotNull]
    public long MachineId { get; set; }

    /// <summary>The associated alert rule.</summary>
    [Association(ThisKey = nameof(AlertRuleId), OtherKey = nameof(AlertRule.Id))]
    public AlertRule? AlertRule { get; set; }

    /// <summary>The associated machine.</summary>
    [Association(ThisKey = nameof(MachineId), OtherKey = nameof(Machine.Id))]
    public Machine? Machine { get; set; }

    /// <summary>When this association was created.</summary>
    [Column("CreatedAt"), NotNull]
    public required DateTimeOffset CreatedAt { get; set; }
}
