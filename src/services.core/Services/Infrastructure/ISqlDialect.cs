// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// Provides database-specific SQL strings for machine health and staleness operations.
/// </summary>
public interface ISqlDialect
{
    /// <summary>
    /// Whether this dialect supports native table partitioning (PostgreSQL PARTITION BY RANGE).
    /// When false, partitioned tables are treated as standard tables.
    /// </summary>
    bool SupportsPartitioning { get; }

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
    /// SQL for sweeping health status recomputation for all machines belonging to a single tenant.
    /// </summary>
    string HealthSweepForTenant { get; }

    /// <summary>
    /// SQL for detecting and marking machines that have gone stale or offline based on
    /// their last ping or telemetry timestamps.
    /// </summary>
    string StaleSweepSql { get; }
}
