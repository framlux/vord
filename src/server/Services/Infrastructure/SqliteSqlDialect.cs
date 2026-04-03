// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// SQLite-specific SQL for MachineState upserts using INSERT OR REPLACE semantics and MAX() instead of GREATEST().
/// Each DO UPDATE SET includes a WHERE guard to prevent stale data from overwriting newer data.
/// </summary>
public sealed class SqliteSqlDialect : ISqlDialect
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
            "Hostname" = excluded."Hostname",
            "HardwareVendor" = excluded."HardwareVendor",
            "HardwareModel" = excluded."HardwareModel",
            "HardwareSerial" = excluded."HardwareSerial",
            "CpuBrand" = excluded."CpuBrand",
            "CpuCores" = excluded."CpuCores",
            "MemoryTotalBytes" = excluded."MemoryTotalBytes",
            "UptimeSeconds" = excluded."UptimeSeconds",
            "BiosVersion" = excluded."BiosVersion",
            "IpAddresses" = excluded."IpAddresses",
            "SystemInfoAt" = excluded."SystemInfoAt",
            "LastTelemetryAt" = MAX("MachineState"."LastTelemetryAt", excluded."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertOsVersion => """
        INSERT INTO "MachineState" ("MachineId", "OsName", "OsVersion", "Kernel", "OsVersionAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @osName, @osVersion, @kernel, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "OsName" = excluded."OsName",
            "OsVersion" = excluded."OsVersion",
            "Kernel" = excluded."Kernel",
            "OsVersionAt" = excluded."OsVersionAt",
            "LastTelemetryAt" = MAX("MachineState"."LastTelemetryAt", excluded."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertCpuInfo => """
        INSERT INTO "MachineState" ("MachineId", "CpuType", "CpuPhysicalCpus", "CpuLogicalCpus", "CpuInfoAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @cpuType, @physCpus, @logCpus, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "CpuType" = excluded."CpuType",
            "CpuPhysicalCpus" = excluded."CpuPhysicalCpus",
            "CpuLogicalCpus" = excluded."CpuLogicalCpus",
            "CpuInfoAt" = excluded."CpuInfoAt",
            "LastTelemetryAt" = MAX("MachineState"."LastTelemetryAt", excluded."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertMemoryInfo => """
        INSERT INTO "MachineState" ("MachineId", "SwapTotalBytes", "SwapFreeBytes", "MemoryInfoAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @swapTotal, @swapFree, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "SwapTotalBytes" = excluded."SwapTotalBytes",
            "SwapFreeBytes" = excluded."SwapFreeBytes",
            "MemoryInfoAt" = excluded."MemoryInfoAt",
            "LastTelemetryAt" = MAX("MachineState"."LastTelemetryAt", excluded."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertDiskInfo => """
        INSERT INTO "MachineState" ("MachineId", "DiskInfos", "DiskInfoAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @diskJson, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "DiskInfos" = excluded."DiskInfos",
            "DiskInfoAt" = excluded."DiskInfoAt",
            "LastTelemetryAt" = MAX("MachineState"."LastTelemetryAt", excluded."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertCpuUsage => """
        INSERT INTO "MachineState" ("MachineId", "CpuUsagePercent", "CpuUsageAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @cpu, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "CpuUsagePercent" = excluded."CpuUsagePercent",
            "CpuUsageAt" = excluded."CpuUsageAt",
            "LastTelemetryAt" = MAX("MachineState"."LastTelemetryAt", excluded."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertMemoryUsage => """
        INSERT INTO "MachineState" ("MachineId", "MemoryUsedBytes", "MemoryUsagePercent", "MemoryUsageAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @memUsed, @memPct, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "MemoryUsedBytes" = excluded."MemoryUsedBytes",
            "MemoryUsagePercent" = excluded."MemoryUsagePercent",
            "MemoryUsageAt" = excluded."MemoryUsageAt",
            "LastTelemetryAt" = MAX("MachineState"."LastTelemetryAt", excluded."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertDiskUsage => """
        INSERT INTO "MachineState" ("MachineId", "DiskUsages", "DiskUsageAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @diskJson, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "DiskUsages" = excluded."DiskUsages",
            "DiskUsageAt" = excluded."DiskUsageAt",
            "LastTelemetryAt" = MAX("MachineState"."LastTelemetryAt", excluded."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertHardwareHealth => """
        INSERT INTO "MachineState" ("MachineId", "HardwareHealth", "HardwareHealthAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @hwJson, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "HardwareHealth" = excluded."HardwareHealth",
            "HardwareHealthAt" = excluded."HardwareHealthAt",
            "LastTelemetryAt" = MAX("MachineState"."LastTelemetryAt", excluded."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertPackageUpdates => """
        INSERT INTO "MachineState" ("MachineId", "PendingUpdates", "SecurityUpdates", "PackageUpdatesAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @pending, @security, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "PendingUpdates" = excluded."PendingUpdates",
            "SecurityUpdates" = excluded."SecurityUpdates",
            "PackageUpdatesAt" = excluded."PackageUpdatesAt",
            "LastTelemetryAt" = MAX("MachineState"."LastTelemetryAt", excluded."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertServiceStatus => """
        INSERT INTO "MachineState" ("MachineId", "TotalServices", "FailedServices", "ServiceStatusAt", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @total, @failed, @ts, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "TotalServices" = excluded."TotalServices",
            "FailedServices" = excluded."FailedServices",
            "ServiceStatusAt" = excluded."ServiceStatusAt",
            "LastTelemetryAt" = MAX("MachineState"."LastTelemetryAt", excluded."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public string UpsertLastTelemetry => """
        INSERT INTO "MachineState" ("MachineId", "LastTelemetryAt", "HealthStatus")
        VALUES (@machineId, @ts, 0)
        ON CONFLICT ("MachineId") DO UPDATE SET
            "LastTelemetryAt" = MAX("MachineState"."LastTelemetryAt", excluded."LastTelemetryAt")
        WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
        """;

    /// <inheritdoc/>
    public (string Sql, string SessionsValue) BuildUpsertSshSessions(string? existingSessions, string newPayload)
    {
        // SQLite doesn't have jsonb array functions, so we merge the arrays in C#
        List<JsonElement> merged = new();

        if (string.IsNullOrEmpty(existingSessions) == false)
        {
            try
            {
                using JsonDocument existingDoc = JsonDocument.Parse(existingSessions);
                if (existingDoc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement elem in existingDoc.RootElement.EnumerateArray())
                    {
                        merged.Add(elem.Clone());
                    }
                }
            }
            catch
            {
                // Ignore malformed existing data
            }
        }

        try
        {
            using JsonDocument newDoc = JsonDocument.Parse(newPayload);
            merged.Add(newDoc.RootElement.Clone());
        }
        catch
        {
            // Ignore malformed new payload
        }

        // Sort by timestamp descending, cap at 50
        List<JsonElement> sorted = merged
            .OrderByDescending(e =>
            {
                if (e.TryGetProperty("timestamp", out JsonElement ts))
                {
                    return ts.GetString() ?? "";
                }

                return "";
            })
            .Take(50)
            .ToList();

        string sessionsJson = JsonSerializer.Serialize(sorted, JsonDefaults.SnakeCase);

        string sql = """
            INSERT INTO "MachineState" ("MachineId", "SshSessions", "SshSessionsAt", "LastTelemetryAt", "HealthStatus")
            VALUES (@machineId, @sshJson, @ts, @ts, 0)
            ON CONFLICT ("MachineId") DO UPDATE SET
                "SshSessions" = @sshJson,
                "SshSessionsAt" = excluded."SshSessionsAt",
                "LastTelemetryAt" = MAX("MachineState"."LastTelemetryAt", excluded."LastTelemetryAt")
            WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL
            """;

        return (sql, sessionsJson);
    }
}
