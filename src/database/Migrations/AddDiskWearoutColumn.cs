// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Records the highest SSD wear figure reported by any disk on a machine, so the health sweep can
/// turn a number the UI already displays into a verdict. The sweep reads scalar columns only, so a
/// value that lives solely in the hardware-health JSON payload can never reach it.
/// </summary>
[MigrationVersion(2026, 09, 17, 1)]
public sealed class AddDiskWearoutColumn : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        // Nullable on purpose: null means no disk has yet reported a usable wear attribute, which
        // is the normal state on NVMe-only hosts and on hosts without smartctl.
        Alter.Table(TableNames.MachineStateSummary)
            .AddColumn("MaxDiskWearoutPercent").AsInt32().Nullable();
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Delete.Column("MaxDiskWearoutPercent").FromTable(TableNames.MachineStateSummary);
    }
}
