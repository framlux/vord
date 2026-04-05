// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// PostgreSQL-specific SQL for machine health sweep and staleness detection.
/// All health computation uses pre-computed scalar columns — no JSONB parsing.
/// </summary>
public sealed class PostgresSqlDialect : ISqlDialect
{
    /// <inheritdoc/>
    public bool SupportsJsonbFilters => true;

    /// <inheritdoc/>
    public bool SupportsJsonbSort => true;

    /// <inheritdoc/>
    public string HealthSweepForTenant => """
        WITH computed AS (
            SELECT "MachineId", CASE
                WHEN "LastSeenAt" IS NULL
                    OR "LastSeenAt" < (NOW() - MAKE_INTERVAL(secs => @onlineThresholdSeconds))
                    THEN 3
                WHEN "CpuUsagePercent" >= 95 OR "MemoryUsagePercent" >= 95
                    THEN 2
                WHEN COALESCE("FailedServices", 0) > 0
                    THEN 2
                WHEN COALESCE("MaxDiskUsagePercent", 0) >= 95
                    THEN 2
                WHEN "HasDiskHealthIssue" = true OR "HasHardwareIssue" = true
                    THEN 2
                WHEN "CpuUsagePercent" >= 80 OR "MemoryUsagePercent" >= 80
                    THEN 1
                WHEN COALESCE("MaxDiskUsagePercent", 0) >= 80
                    THEN 1
                ELSE 0
            END AS "NewHealth"
            FROM "MachineStateSummary"
            WHERE "TenantId" = @tenantId
        )
        UPDATE "MachineStateSummary" s SET "HealthStatus" = c."NewHealth"
        FROM computed c
        WHERE s."MachineId" = c."MachineId" AND s."HealthStatus" != c."NewHealth"
        """;

    /// <inheritdoc/>
    public string StaleSweepSql => """
        UPDATE "MachineStateSummary"
        SET "HealthStatus" = 3
        WHERE "LastSeenAt" < (NOW() - MAKE_INTERVAL(secs => @onlineThresholdSeconds))
          AND "HealthStatus" != 3
        """;
}
