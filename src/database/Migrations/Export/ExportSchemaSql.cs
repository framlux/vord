// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Migrations.Export;

/// <summary>
/// Provides the SQLite DDL for creating the export schema.
/// This SQL is derived from <see cref="ExportInitialMigration"/> and kept in sync with it.
/// The FluentMigrator migration class serves as the canonical schema definition;
/// this static SQL is used at runtime to avoid FluentMigrator assembly scanning issues.
/// </summary>
public static class ExportSchemaSql
{
    /// <summary>
    /// DDL SQL to create all export schema tables and indexes.
    /// </summary>
    public const string CreateSchema = """
        CREATE TABLE "Machines" (
            "Id" INTEGER PRIMARY KEY NOT NULL,
            "Name" TEXT NOT NULL,
            "Description" TEXT,
            "Location" TEXT,
            "SerialNumber" TEXT NOT NULL,
            "SystemId" TEXT NOT NULL,
            "AssetTagNumber" TEXT,
            "MachineType" INTEGER NOT NULL,
            "OperatingSystem" INTEGER NOT NULL,
            "RegisteredOn" TEXT NOT NULL
        );

        CREATE TABLE "MachineStateSummary" (
            "MachineId" INTEGER PRIMARY KEY NOT NULL,
            "TenantId" INTEGER NOT NULL,
            "Name" TEXT NOT NULL,
            "OperatingSystem" INTEGER NOT NULL,
            "MachineType" INTEGER NOT NULL,
            "Hostname" TEXT,
            "HardwareModel" TEXT,
            "IpAddresses" TEXT,
            "OsName" TEXT,
            "OsVersion" TEXT,
            "CpuUsagePercent" INTEGER,
            "MemoryUsagePercent" INTEGER,
            "MaxDiskUsagePercent" INTEGER,
            "PendingUpdates" INTEGER,
            "SecurityUpdates" INTEGER,
            "FailedServices" INTEGER,
            "TotalServices" INTEGER,
            "HasDiskHealthIssue" INTEGER,
            "HasHardwareIssue" INTEGER,
            "HealthStatus" INTEGER NOT NULL DEFAULT 0,
            "LastSeenAt" TEXT,
            FOREIGN KEY ("MachineId") REFERENCES "Machines"("Id")
        );

        CREATE TABLE "MachineStateDetail" (
            "MachineId" INTEGER PRIMARY KEY NOT NULL,
            "HardwareVendor" TEXT,
            "HardwareSerial" TEXT,
            "CpuBrand" TEXT,
            "CpuCores" INTEGER,
            "MemoryTotalBytes" INTEGER,
            "UptimeSeconds" INTEGER,
            "BiosVersion" TEXT,
            "Kernel" TEXT,
            "CpuType" TEXT,
            "CpuPhysicalCpus" INTEGER,
            "CpuLogicalCpus" INTEGER,
            "SwapTotalBytes" INTEGER,
            "SwapFreeBytes" INTEGER,
            "MemoryUsedBytes" INTEGER,
            "DiskInfos" TEXT,
            "DiskUsages" TEXT,
            "SshSessions" TEXT,
            "HardwareHealth" TEXT,
            FOREIGN KEY ("MachineId") REFERENCES "Machines"("Id")
        );

        CREATE TABLE "MachineTelemetry" (
            "Id" INTEGER PRIMARY KEY NOT NULL,
            "MachineId" INTEGER NOT NULL,
            "TelemetryType" INTEGER NOT NULL,
            "Payload" TEXT NOT NULL,
            "ReceivedAt" TEXT NOT NULL,
            "SourceEventId" TEXT,
            FOREIGN KEY ("MachineId") REFERENCES "Machines"("Id")
        );

        CREATE INDEX "idx_telemetry_machine" ON "MachineTelemetry"("MachineId");
        CREATE INDEX "idx_telemetry_type" ON "MachineTelemetry"("MachineId", "TelemetryType");
        CREATE INDEX "idx_telemetry_received" ON "MachineTelemetry"("ReceivedAt");

        CREATE TABLE "AuditLog" (
            "Id" INTEGER PRIMARY KEY NOT NULL,
            "UserId" INTEGER,
            "MachineId" INTEGER,
            "Action" INTEGER NOT NULL,
            "ResourceType" INTEGER NOT NULL,
            "ResourceId" TEXT,
            "Details" TEXT,
            "IpAddress" TEXT,
            "Timestamp" TEXT NOT NULL
        );

        CREATE INDEX "idx_auditlog_timestamp" ON "AuditLog"("Timestamp" DESC);

        CREATE TABLE "ExportMetadata" (
            "Key" TEXT PRIMARY KEY NOT NULL,
            "Value" TEXT NOT NULL
        );
        """;
}
