// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Adds indexes on MachineState scalar columns used by the machine search endpoint
/// for filtering and sorting. Without these, search queries on large fleets require
/// full table scans on CpuUsagePercent, MemoryUsagePercent, PendingUpdates,
/// SecurityUpdates, and FailedServices.
/// </summary>
[MigrationVersion(2026, 4, 4, 1)]
public sealed class AddSearchIndexesMigration : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        Create.Index("IX_MachineState_CpuUsagePercent")
            .OnTable(TableNames.MachineState)
            .OnColumn("CpuUsagePercent").Ascending();

        Create.Index("IX_MachineState_MemoryUsagePercent")
            .OnTable(TableNames.MachineState)
            .OnColumn("MemoryUsagePercent").Ascending();

        Create.Index("IX_MachineState_PendingUpdates")
            .OnTable(TableNames.MachineState)
            .OnColumn("PendingUpdates").Ascending();

        Create.Index("IX_MachineState_SecurityUpdates")
            .OnTable(TableNames.MachineState)
            .OnColumn("SecurityUpdates").Ascending();

        Create.Index("IX_MachineState_FailedServices")
            .OnTable(TableNames.MachineState)
            .OnColumn("FailedServices").Ascending();
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Delete.Index("IX_MachineState_CpuUsagePercent").OnTable(TableNames.MachineState);
        Delete.Index("IX_MachineState_MemoryUsagePercent").OnTable(TableNames.MachineState);
        Delete.Index("IX_MachineState_PendingUpdates").OnTable(TableNames.MachineState);
        Delete.Index("IX_MachineState_SecurityUpdates").OnTable(TableNames.MachineState);
        Delete.Index("IX_MachineState_FailedServices").OnTable(TableNames.MachineState);
    }
}
