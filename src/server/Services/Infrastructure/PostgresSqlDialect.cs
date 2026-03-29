// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// PostgreSQL-specific SQL for MachineState upserts using JSONB and GREATEST().
/// Each DO UPDATE SET includes a WHERE guard to prevent stale data from overwriting newer data.
/// </summary>
public sealed class PostgresSqlDialect : ISqlDialect
{
    /// <inheritdoc/>
    public string UpsertSystemInfo => """
        INSERT INTO "MachineState" ("MachineId", "Hostname", "HardwareVendor", "HardwareModel", "HardwareSerial",
            "CpuBrand", "CpuCores", "MemoryTotalBytes", "UptimeSeconds", "BiosVersion", "IpAddresses",
            "SystemInfoAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @hostname, @vendor, @model, @serial,
            @cpuBrand, @cores, @memory, @uptime, @bios, @ips,
            @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "Hostname" = EXCLUDED."Hostname",
            "HardwareVendor" = EXCLUDED."HardwareVendor",
            "HardwareModel" = EXCLUDED."HardwareModel",
            "HardwareSerial" = EXCLUDED."HardwareSerial",
            "CpuBrand" = EXCLUDED."CpuBrand",
            "CpuCores" = EXCLUDED."CpuCores",
            "MemoryTotalBytes" = EXCLUDED."MemoryTotalBytes",
            "UptimeSeconds" = EXCLUDED."UptimeSeconds",
            "BiosVersion" = EXCLUDED."BiosVersion",
            "IpAddresses" = EXCLUDED."IpAddresses",
            "SystemInfoAt" = EXCLUDED."SystemInfoAt",
            "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertOsVersion => """
        INSERT INTO "MachineState" ("MachineId", "OsName", "OsVersion", "Kernel", "OsVersionAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @osName, @osVersion, @kernel, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "OsName" = EXCLUDED."OsName",
            "OsVersion" = EXCLUDED."OsVersion",
            "Kernel" = EXCLUDED."Kernel",
            "OsVersionAt" = EXCLUDED."OsVersionAt",
            "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertCpuInfo => """
        INSERT INTO "MachineState" ("MachineId", "CpuType", "CpuPhysicalCpus", "CpuLogicalCpus", "CpuInfoAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @cpuType, @physCpus, @logCpus, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "CpuType" = EXCLUDED."CpuType",
            "CpuPhysicalCpus" = EXCLUDED."CpuPhysicalCpus",
            "CpuLogicalCpus" = EXCLUDED."CpuLogicalCpus",
            "CpuInfoAt" = EXCLUDED."CpuInfoAt",
            "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertMemoryInfo => """
        INSERT INTO "MachineState" ("MachineId", "SwapTotalBytes", "SwapFreeBytes", "MemoryInfoAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @swapTotal, @swapFree, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "SwapTotalBytes" = EXCLUDED."SwapTotalBytes",
            "SwapFreeBytes" = EXCLUDED."SwapFreeBytes",
            "MemoryInfoAt" = EXCLUDED."MemoryInfoAt",
            "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertDiskInfo => """
        INSERT INTO "MachineState" ("MachineId", "DiskInfos", "DiskInfoAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @diskJson::jsonb, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "DiskInfos" = EXCLUDED."DiskInfos",
            "DiskInfoAt" = EXCLUDED."DiskInfoAt",
            "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertCpuUsage => """
        INSERT INTO "MachineState" ("MachineId", "CpuUsagePercent", "CpuUsageAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @cpu, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "CpuUsagePercent" = EXCLUDED."CpuUsagePercent",
            "CpuUsageAt" = EXCLUDED."CpuUsageAt",
            "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertMemoryUsage => """
        INSERT INTO "MachineState" ("MachineId", "MemoryUsedBytes", "MemoryUsagePercent", "MemoryUsageAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @memUsed, @memPct, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "MemoryUsedBytes" = EXCLUDED."MemoryUsedBytes",
            "MemoryUsagePercent" = EXCLUDED."MemoryUsagePercent",
            "MemoryUsageAt" = EXCLUDED."MemoryUsageAt",
            "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertDiskUsage => """
        INSERT INTO "MachineState" ("MachineId", "DiskUsages", "DiskUsageAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @diskJson::jsonb, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "DiskUsages" = EXCLUDED."DiskUsages",
            "DiskUsageAt" = EXCLUDED."DiskUsageAt",
            "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertHardwareHealth => """
        INSERT INTO "MachineState" ("MachineId", "HardwareHealth", "HardwareHealthAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @hwJson::jsonb, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "HardwareHealth" = EXCLUDED."HardwareHealth",
            "HardwareHealthAt" = EXCLUDED."HardwareHealthAt",
            "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertPackageUpdates => """
        INSERT INTO "MachineState" ("MachineId", "PendingUpdates", "SecurityUpdates", "PackageUpdatesAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @pending, @security, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "PendingUpdates" = EXCLUDED."PendingUpdates",
            "SecurityUpdates" = EXCLUDED."SecurityUpdates",
            "PackageUpdatesAt" = EXCLUDED."PackageUpdatesAt",
            "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertServiceStatus => """
        INSERT INTO "MachineState" ("MachineId", "TotalServices", "FailedServices", "ServiceStatusAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @total, @failed, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "TotalServices" = EXCLUDED."TotalServices",
            "FailedServices" = EXCLUDED."FailedServices",
            "ServiceStatusAt" = EXCLUDED."ServiceStatusAt",
            "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertLastTelemetry => """
        INSERT INTO "MachineState" ("MachineId", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public (string Sql, string SessionsValue) BuildUpsertSshSessions(string? existingSessions, string newPayload)
    {
        string sql = """
            INSERT INTO "MachineState" ("MachineId", "SshSessions", "SshSessionsAt", "LastTelemetryAt", "HealthStatus")
            VALUES (@machineId, jsonb_build_array(@sshJson::jsonb), @ts, @ts, 0)
            ON CONFLICT ("MachineId") DO UPDATE SET
                "SshSessions" = (
                    SELECT jsonb_agg(elem)
                    FROM (
                        SELECT elem
                        FROM jsonb_array_elements(COALESCE("MachineState"."SshSessions", '[]'::jsonb) || jsonb_build_array(@sshJson::jsonb)) AS elem
                        ORDER BY elem->>'timestamp' DESC
                        LIMIT 50
                    ) sub
                ),
                "SshSessionsAt" = EXCLUDED."SshSessionsAt",
                "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
            WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
            """;

        return (sql, newPayload);
    }
}
