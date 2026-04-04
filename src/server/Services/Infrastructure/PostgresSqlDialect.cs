// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// PostgreSQL-specific SQL for MachineState upserts using JSONB and GREATEST().
/// Each DO UPDATE SET includes a per-type WHERE guard to prevent stale data from overwriting
/// newer data of the same type. LastTelemetryAt is always advanced via GREATEST() but is not
/// used as a guard — each type guards against its own timestamp column to avoid cross-type
/// interference under concurrent writes.
/// </summary>
public sealed class PostgresSqlDialect : ISqlDialect
{
    /// <inheritdoc/>
    public string UpdateLastPing => """
        UPDATE "MachineState" SET "LastPingAt" = @ts
        WHERE "MachineId" = @machineId
        """;

    /// <inheritdoc/>
    public string RecomputeHealthStatus => """
        UPDATE "MachineState" SET "HealthStatus" = CASE
            -- Offline: no recent ping
            WHEN "LastPingAt" IS NULL
                OR "LastPingAt" < (NOW() - MAKE_INTERVAL(secs => @onlineThresholdSeconds))
                THEN 3
            -- Critical: scalar metrics
            WHEN "CpuUsagePercent" >= 95 OR "MemoryUsagePercent" >= 95
                THEN 2
            WHEN COALESCE("FailedServices", 0) > 0
                THEN 2
            -- Critical: disk usage from JSONB
            WHEN "DiskUsages" IS NOT NULL AND EXISTS (
                SELECT 1 FROM jsonb_array_elements("DiskUsages") d
                WHERE (d->>'usage_percent')::int >= 95)
                THEN 2
            -- Critical: hardware issues from JSONB
            WHEN "HardwareHealth" IS NOT NULL AND (
                EXISTS (SELECT 1 FROM jsonb_array_elements("HardwareHealth"->'disk_smart') d
                    WHERE UPPER(d->>'health_status') = 'FAILED')
                OR EXISTS (SELECT 1 FROM jsonb_array_elements("HardwareHealth"->'fans') f
                    WHERE (f->>'rpm')::int = 0)
                OR EXISTS (SELECT 1 FROM jsonb_array_elements("HardwareHealth"->'power_supplies') p
                    WHERE UPPER(p->>'status') != 'OK'))
                THEN 2
            -- Warning: scalar metrics
            WHEN "CpuUsagePercent" >= 80 OR "MemoryUsagePercent" >= 80
                THEN 1
            -- Warning: disk usage from JSONB
            WHEN "DiskUsages" IS NOT NULL AND EXISTS (
                SELECT 1 FROM jsonb_array_elements("DiskUsages") d
                WHERE (d->>'usage_percent')::int >= 80)
                THEN 1
            -- Warning: disk wear/temp from JSONB
            WHEN "HardwareHealth" IS NOT NULL AND EXISTS (
                SELECT 1 FROM jsonb_array_elements("HardwareHealth"->'disk_smart') d
                WHERE (d->>'wearout_percent')::int > 80 OR (d->>'temperature_celsius')::int >= 55)
                THEN 1
            -- Healthy
            ELSE 0
        END
        WHERE "MachineId" = @machineId
        """;

    /// <inheritdoc/>
    public string RecomputeAllHealthStatuses => """
        UPDATE "MachineState" SET "HealthStatus" = CASE
            WHEN "LastPingAt" IS NULL
                OR "LastPingAt" < (NOW() - MAKE_INTERVAL(secs => @onlineThresholdSeconds))
                THEN 3
            WHEN "CpuUsagePercent" >= 95 OR "MemoryUsagePercent" >= 95
                THEN 2
            WHEN COALESCE("FailedServices", 0) > 0
                THEN 2
            WHEN "DiskUsages" IS NOT NULL AND EXISTS (
                SELECT 1 FROM jsonb_array_elements("DiskUsages") d
                WHERE (d->>'usage_percent')::int >= 95)
                THEN 2
            WHEN "HardwareHealth" IS NOT NULL AND (
                EXISTS (SELECT 1 FROM jsonb_array_elements("HardwareHealth"->'disk_smart') d
                    WHERE UPPER(d->>'health_status') = 'FAILED')
                OR EXISTS (SELECT 1 FROM jsonb_array_elements("HardwareHealth"->'fans') f
                    WHERE (f->>'rpm')::int = 0)
                OR EXISTS (SELECT 1 FROM jsonb_array_elements("HardwareHealth"->'power_supplies') p
                    WHERE UPPER(p->>'status') != 'OK'))
                THEN 2
            WHEN "CpuUsagePercent" >= 80 OR "MemoryUsagePercent" >= 80
                THEN 1
            WHEN "DiskUsages" IS NOT NULL AND EXISTS (
                SELECT 1 FROM jsonb_array_elements("DiskUsages") d
                WHERE (d->>'usage_percent')::int >= 80)
                THEN 1
            WHEN "HardwareHealth" IS NOT NULL AND EXISTS (
                SELECT 1 FROM jsonb_array_elements("HardwareHealth"->'disk_smart') d
                WHERE (d->>'wearout_percent')::int > 80 OR (d->>'temperature_celsius')::int >= 55)
                THEN 1
            ELSE 0
        END
        """;

    /// <inheritdoc/>
    public bool SupportsJsonbFilters => true;

    /// <inheritdoc/>
    public bool SupportsJsonbSort => true;

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
        WHERE @ts >= "MachineState"."SystemInfoAt" OR "MachineState"."SystemInfoAt" IS NULL
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
        WHERE @ts >= "MachineState"."OsVersionAt" OR "MachineState"."OsVersionAt" IS NULL
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
        WHERE @ts >= "MachineState"."CpuInfoAt" OR "MachineState"."CpuInfoAt" IS NULL
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
        WHERE @ts >= "MachineState"."MemoryInfoAt" OR "MachineState"."MemoryInfoAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertDiskInfo => """
        INSERT INTO "MachineState" ("MachineId", "DiskInfos", "DiskInfoAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @diskJson::jsonb, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "DiskInfos" = EXCLUDED."DiskInfos",
            "DiskInfoAt" = EXCLUDED."DiskInfoAt",
            "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
        WHERE @ts >= "MachineState"."DiskInfoAt" OR "MachineState"."DiskInfoAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertCpuUsage => """
        INSERT INTO "MachineState" ("MachineId", "CpuUsagePercent", "CpuUsageAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @cpu, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "CpuUsagePercent" = EXCLUDED."CpuUsagePercent",
            "CpuUsageAt" = EXCLUDED."CpuUsageAt",
            "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
        WHERE @ts >= "MachineState"."CpuUsageAt" OR "MachineState"."CpuUsageAt" IS NULL
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
        WHERE @ts >= "MachineState"."MemoryUsageAt" OR "MachineState"."MemoryUsageAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertDiskUsage => """
        INSERT INTO "MachineState" ("MachineId", "DiskUsages", "DiskUsageAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @diskJson::jsonb, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "DiskUsages" = EXCLUDED."DiskUsages",
            "DiskUsageAt" = EXCLUDED."DiskUsageAt",
            "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
        WHERE @ts >= "MachineState"."DiskUsageAt" OR "MachineState"."DiskUsageAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertHardwareHealth => """
        INSERT INTO "MachineState" ("MachineId", "HardwareHealth", "HardwareHealthAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @hwJson::jsonb, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "HardwareHealth" = EXCLUDED."HardwareHealth",
            "HardwareHealthAt" = EXCLUDED."HardwareHealthAt",
            "LastTelemetryAt" = GREATEST("MachineState"."LastTelemetryAt", EXCLUDED."LastTelemetryAt")
        WHERE @ts >= "MachineState"."HardwareHealthAt" OR "MachineState"."HardwareHealthAt" IS NULL
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
        WHERE @ts >= "MachineState"."PackageUpdatesAt" OR "MachineState"."PackageUpdatesAt" IS NULL
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
        WHERE @ts >= "MachineState"."ServiceStatusAt" OR "MachineState"."ServiceStatusAt" IS NULL
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
            WHERE @ts >= "MachineState"."SshSessionsAt" OR "MachineState"."SshSessionsAt" IS NULL
            """;

        return (sql, newPayload);
    }
}
