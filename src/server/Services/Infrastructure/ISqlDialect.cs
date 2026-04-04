// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// Provides database-specific SQL strings for MachineState upsert operations.
/// </summary>
public interface ISqlDialect
{
    /// <summary>SQL for upserting SystemInfo telemetry (type=1).</summary>
    string UpsertSystemInfo { get; }

    /// <summary>SQL for upserting OsVersion telemetry (type=2).</summary>
    string UpsertOsVersion { get; }

    /// <summary>SQL for upserting CpuInfo telemetry (type=3).</summary>
    string UpsertCpuInfo { get; }

    /// <summary>SQL for upserting MemoryInfo telemetry (type=4).</summary>
    string UpsertMemoryInfo { get; }

    /// <summary>SQL for upserting DiskInfo telemetry (type=5).</summary>
    string UpsertDiskInfo { get; }

    /// <summary>SQL for upserting CpuUsage telemetry (type=6).</summary>
    string UpsertCpuUsage { get; }

    /// <summary>SQL for upserting MemoryUsage telemetry (type=7).</summary>
    string UpsertMemoryUsage { get; }

    /// <summary>SQL for upserting DiskUsage telemetry (type=8).</summary>
    string UpsertDiskUsage { get; }

    /// <summary>SQL for upserting HardwareHealth telemetry (type=10).</summary>
    string UpsertHardwareHealth { get; }

    /// <summary>SQL for upserting PackageUpdates telemetry (type=11).</summary>
    string UpsertPackageUpdates { get; }

    /// <summary>SQL for upserting ServiceStatus telemetry (type=12).</summary>
    string UpsertServiceStatus { get; }

    /// <summary>SQL for upserting only LastTelemetryAt (unknown type fallback).</summary>
    string UpsertLastTelemetry { get; }

    /// <summary>SQL for updating LastPingAt on a MachineState row.</summary>
    string UpdateLastPing { get; }

    /// <summary>
    /// SQL for recomputing the HealthStatus column from scalar metrics on MachineState.
    /// Called after batch updates to keep HealthStatus current for SQL-level fleet queries.
    /// Uses parameter @machineId and @onlineThresholdSeconds.
    /// </summary>
    string RecomputeHealthStatus { get; }

    /// <summary>
    /// SQL for bulk-recomputing HealthStatus for all machines whose status may be stale.
    /// Used by the periodic health recomputation service to keep fleet overview counts accurate.
    /// Uses parameter @onlineThresholdSeconds.
    /// </summary>
    string RecomputeAllHealthStatuses { get; }

    /// <summary>
    /// Whether this dialect supports JSONB filter expressions in LINQ queries (PostgreSQL only).
    /// When false, JSONB-based filters (disk usage ranges, hardware issues) must be evaluated in memory.
    /// </summary>
    bool SupportsJsonbFilters { get; }

    /// <summary>
    /// Whether this dialect supports JSONB sort expressions (e.g., sorting by max disk usage).
    /// When false, sort-by-disk requires the full-scan in-memory path.
    /// </summary>
    bool SupportsJsonbSort { get; }

    /// <summary>
    /// Builds the SQL for upserting SSH sessions (type=9).
    /// SSH sessions require special handling because PostgreSQL uses jsonb array functions
    /// while SQLite handles JSON manipulation in C# before inserting.
    /// </summary>
    /// <param name="existingSessions">The current SSH sessions JSON from the database (for SQLite pre-processing). Ignored by PostgreSQL.</param>
    /// <param name="newPayload">The new SSH session JSON payload.</param>
    /// <returns>The SQL string and the processed sessions JSON value to bind.</returns>
    (string Sql, string SessionsValue) BuildUpsertSshSessions(string? existingSessions, string newPayload);
}
