// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// SQLite-specific SQL for machine health sweep and staleness detection.
/// Uses SQLite date functions (datetime) instead of PostgreSQL MAKE_INTERVAL.
/// All health computation uses pre-computed scalar columns — no JSONB parsing.
/// </summary>
public sealed class SqliteSqlDialect : ISqlDialect
{
    /// <inheritdoc/>
    public bool SupportsPartitioning => false;

    /// <inheritdoc/>
    public bool SupportsJsonbFilters => false;

    /// <inheritdoc/>
    public bool SupportsJsonbSort => false;

    /// <inheritdoc/>
    public string HealthSweepForTenant => $"""
        UPDATE "MachineStateSummary"
        SET "HealthStatus" = {HealthExpression},
            "TelemetryStale" = {StaleExpression}
        WHERE "TenantId" = @tenantId
          AND ("HealthStatus" != {HealthExpression} OR "TelemetryStale" != {StaleExpression})
        """;

    // SQLite has no UPDATE ... FROM, so the whole expression appears twice — once in SET and once
    // in the change guard. One constant per verdict keeps that from becoming two transcriptions of
    // the rule in a file that is already the second transcription of it.
    private const string StaleExpression = """
        CASE
            WHEN "LastSeenAt" IS NULL
                OR "LastSeenAt" < datetime('now', '-' || @staleSeconds || ' seconds')
                THEN 1
            ELSE 0
        END
        """;

    // The two-argument MAX() scalar returns NULL if either argument is NULL, so both timestamps are
    // coalesced to a sentinel that sorts before any real value rather than compared directly.
    private const string HealthExpression = """
        CASE
            WHEN MAX(COALESCE("LastSeenAt", '0001-01-01 00:00:00'),
                     COALESCE("LastHeartbeatAt", '0001-01-01 00:00:00'))
                 < datetime('now', '-' || @onlineThresholdSeconds || ' seconds')
                THEN 3
            ELSE MAX(
                CASE
                    WHEN "CpuUsagePercent" >= 95 OR "MemoryUsagePercent" >= 95 THEN 2
                    WHEN COALESCE("FailedServices", 0) > 0 THEN 2
                    WHEN COALESCE("MaxDiskUsagePercent", 0) >= 95 THEN 2
                    WHEN "HasDiskHealthIssue" = 1 OR "HasHardwareIssue" = 1 THEN 2
                    WHEN "CpuUsagePercent" >= 80 OR "MemoryUsagePercent" >= 80 THEN 1
                    WHEN COALESCE("MaxDiskUsagePercent", 0) >= 80 THEN 1
                    ELSE 0
                END,
                CASE
                    WHEN "LastSeenAt" IS NULL
                        OR "LastSeenAt" < datetime('now', '-' || @staleSeconds || ' seconds')
                        THEN 1
                    ELSE 0
                END)
        END
        """;
}
