// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations.Export;

/// <summary>
/// Initial export migration that creates the tables used in portable SQLite export files.
/// Tagged with "Export" so it never runs against production databases.
/// </summary>
[ExportMigrationVersion(2026, 03, 10, 1)]
[Tags("Export")]
public sealed class ExportInitialMigration : Migration
{
    /// <summary>
    /// Creates the export schema tables.
    /// </summary>
    public override void Up()
    {
        Create.Table("Machines")
            .WithColumn("Id").AsInt64().PrimaryKey().NotNullable()
            .WithColumn("Name").AsString(250).NotNullable()
            .WithColumn("Description").AsString().Nullable()
            .WithColumn("Location").AsString(250).Nullable()
            .WithColumn("SerialNumber").AsString(64).NotNullable()
            .WithColumn("SystemId").AsString(64).NotNullable()
            .WithColumn("AssetTagNumber").AsString(64).Nullable()
            .WithColumn("MachineType").AsByte().NotNullable()
            .WithColumn("OperatingSystem").AsByte().NotNullable()
            .WithColumn("RegisteredOn").AsString().NotNullable();

        Create.Table("MachineState")
            .WithColumn("MachineId").AsInt64().PrimaryKey().NotNullable()
                .ForeignKey("FK_MachineState_Machines", "Machines", "Id")
            .WithColumn("Hostname").AsString(255).Nullable()
            .WithColumn("HardwareVendor").AsString(255).Nullable()
            .WithColumn("HardwareModel").AsString(255).Nullable()
            .WithColumn("HardwareSerial").AsString(255).Nullable()
            .WithColumn("CpuBrand").AsString(255).Nullable()
            .WithColumn("CpuCores").AsInt32().Nullable()
            .WithColumn("MemoryTotalBytes").AsInt64().Nullable()
            .WithColumn("UptimeSeconds").AsInt64().Nullable()
            .WithColumn("BiosVersion").AsString(64).Nullable()
            .WithColumn("IpAddresses").AsString().Nullable()
            .WithColumn("OsName").AsString(255).Nullable()
            .WithColumn("OsVersion").AsString(64).Nullable()
            .WithColumn("Kernel").AsString(255).Nullable()
            .WithColumn("CpuUsagePercent").AsInt32().Nullable()
            .WithColumn("MemoryUsedBytes").AsInt64().Nullable()
            .WithColumn("MemoryUsagePercent").AsInt32().Nullable()
            .WithColumn("DiskUsages").AsString().Nullable()
            .WithColumn("HardwareHealth").AsString().Nullable()
            .WithColumn("CpuType").AsString(64).Nullable()
            .WithColumn("CpuPhysicalCpus").AsInt32().Nullable()
            .WithColumn("CpuLogicalCpus").AsInt32().Nullable()
            .WithColumn("SwapTotalBytes").AsInt64().Nullable()
            .WithColumn("SwapFreeBytes").AsInt64().Nullable()
            .WithColumn("DiskInfos").AsString().Nullable()
            .WithColumn("SshSessions").AsString().Nullable()
            .WithColumn("PendingUpdates").AsInt32().Nullable()
            .WithColumn("SecurityUpdates").AsInt32().Nullable()
            .WithColumn("TotalServices").AsInt32().Nullable()
            .WithColumn("FailedServices").AsInt32().Nullable()
            .WithColumn("HealthStatus").AsInt16().NotNullable().WithDefaultValue(0)
            .WithColumn("SystemInfoAt").AsString().Nullable()
            .WithColumn("OsVersionAt").AsString().Nullable()
            .WithColumn("CpuUsageAt").AsString().Nullable()
            .WithColumn("MemoryUsageAt").AsString().Nullable()
            .WithColumn("DiskUsageAt").AsString().Nullable()
            .WithColumn("HardwareHealthAt").AsString().Nullable()
            .WithColumn("PackageUpdatesAt").AsString().Nullable()
            .WithColumn("ServiceStatusAt").AsString().Nullable()
            .WithColumn("CpuInfoAt").AsString().Nullable()
            .WithColumn("MemoryInfoAt").AsString().Nullable()
            .WithColumn("DiskInfoAt").AsString().Nullable()
            .WithColumn("SshSessionsAt").AsString().Nullable()
            .WithColumn("LastTelemetryAt").AsString().Nullable();

        Create.Table("MachineTelemetry")
            .WithColumn("Id").AsInt64().PrimaryKey().NotNullable()
            .WithColumn("MachineId").AsInt64().NotNullable()
                .ForeignKey("FK_MachineTelemetry_Machines", "Machines", "Id")
            .WithColumn("TelemetryType").AsInt16().NotNullable()
            .WithColumn("Payload").AsString().NotNullable()
            .WithColumn("ReceivedAt").AsString().NotNullable()
            .WithColumn("SourceEventId").AsString(64).Nullable();

        Create.Index("idx_telemetry_machine")
            .OnTable("MachineTelemetry")
            .OnColumn("MachineId").Ascending();

        Create.Index("idx_telemetry_type")
            .OnTable("MachineTelemetry")
            .OnColumn("MachineId").Ascending()
            .OnColumn("TelemetryType").Ascending();

        Create.Index("idx_telemetry_received")
            .OnTable("MachineTelemetry")
            .OnColumn("ReceivedAt").Ascending();

        Create.Table("ExportMetadata")
            .WithColumn("Key").AsString().PrimaryKey().NotNullable()
            .WithColumn("Value").AsString().NotNullable();
    }

    /// <summary>
    /// Reverts the export schema.
    /// </summary>
    public override void Down()
    {
        Delete.Table("ExportMetadata");
        Delete.Table("MachineTelemetry");
        Delete.Table("MachineState");
        Delete.Table("Machines");
    }
}
