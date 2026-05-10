// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Creates the AlertRuleMachines join table for scoping alert rules to specific machines.
/// </summary>
[MigrationVersion(2026, 05, 10, 1)]
public sealed class AlertRuleMachinesMigration : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        Execute.Sql($"""
            CREATE TABLE "{TableNames.AlertRuleMachines}" (
                "AlertRuleId" INTEGER NOT NULL,
                "MachineId" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                PRIMARY KEY ("AlertRuleId", "MachineId"),
                FOREIGN KEY ("AlertRuleId") REFERENCES "{TableNames.AlertRules}" ("Id") ON DELETE CASCADE,
                FOREIGN KEY ("MachineId") REFERENCES "{TableNames.Machines}" ("Id")
            )
            """);

        Create.Index("IX_AlertRuleMachines_MachineId")
            .OnTable(TableNames.AlertRuleMachines)
            .OnColumn("MachineId").Ascending()
            .OnColumn("AlertRuleId").Ascending();
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Delete.Table(TableNames.AlertRuleMachines);
    }
}
