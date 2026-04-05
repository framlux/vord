// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// SQLite-specific SQL for machine health sweep and staleness detection.
/// Uses SQLite date functions (datetime) instead of PostgreSQL MAKE_INTERVAL.
/// All health computation uses pre-computed scalar columns — no JSONB parsing.
/// </summary>
public sealed class SqliteSqlDialect : ISqlDialect
{
    /// <inheritdoc/>
    public bool SupportsJsonbFilters => false;

    /// <inheritdoc/>
    public bool SupportsJsonbSort => false;

    /// <inheritdoc/>
    public string HealthSweepForTenant => """
        UPDATE "MachineStateSummary"
        SET "HealthStatus" = CASE
            WHEN "LastSeenAt" IS NULL
                OR "LastSeenAt" < datetime('now', '-' || @onlineThresholdSeconds || ' seconds')
                THEN 3
            WHEN "CpuUsagePercent" >= 95 OR "MemoryUsagePercent" >= 95
                THEN 2
            WHEN COALESCE("FailedServices", 0) > 0
                THEN 2
            WHEN COALESCE("MaxDiskUsagePercent", 0) >= 95
                THEN 2
            WHEN "HasDiskHealthIssue" = 1 OR "HasHardwareIssue" = 1
                THEN 2
            WHEN "CpuUsagePercent" >= 80 OR "MemoryUsagePercent" >= 80
                THEN 1
            WHEN COALESCE("MaxDiskUsagePercent", 0) >= 80
                THEN 1
            ELSE 0
        END
        WHERE "TenantId" = @tenantId
          AND "HealthStatus" != CASE
            WHEN "LastSeenAt" IS NULL
                OR "LastSeenAt" < datetime('now', '-' || @onlineThresholdSeconds || ' seconds')
                THEN 3
            WHEN "CpuUsagePercent" >= 95 OR "MemoryUsagePercent" >= 95
                THEN 2
            WHEN COALESCE("FailedServices", 0) > 0
                THEN 2
            WHEN COALESCE("MaxDiskUsagePercent", 0) >= 95
                THEN 2
            WHEN "HasDiskHealthIssue" = 1 OR "HasHardwareIssue" = 1
                THEN 2
            WHEN "CpuUsagePercent" >= 80 OR "MemoryUsagePercent" >= 80
                THEN 1
            WHEN COALESCE("MaxDiskUsagePercent", 0) >= 80
                THEN 1
            ELSE 0
        END
        """;

    /// <inheritdoc/>
    public string StaleSweepSql => """
        UPDATE "MachineStateSummary"
        SET "HealthStatus" = 3
        WHERE "LastSeenAt" < datetime('now', '-' || @onlineThresholdSeconds || ' seconds')
          AND "HealthStatus" != 3
        """;
}
