// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Test.Services.Machines;

/// <summary>
/// Tests covering the health sweep SQL correctness: per-tenant partitioning,
/// scalar-only computation, and offline detection.
/// </summary>
public class PipelineReArchitectureTests
{
    /// <summary>
    /// Verifies that the HealthSweepForTenant SQL contains CASE branches for both critical
    /// (>= 95) and warning (>= 80) thresholds on scalar metrics.
    /// </summary>
    [Test]
    public async Task PostgresDialect_HealthSweep_ContainsCriticalAndWarningChecks()
    {
        PostgresSqlDialect dialect = new();
        string sql = dialect.HealthSweepForTenant;

        bool hasCpuCritical = sql.Contains("\"CpuUsagePercent\" >= 95", StringComparison.Ordinal);
        bool hasMemoryCritical = sql.Contains("\"MemoryUsagePercent\" >= 95", StringComparison.Ordinal);

        await Assert.That(hasCpuCritical).IsTrue()
            .Because("HealthSweepForTenant must check CPU critical threshold (>= 95)");
        await Assert.That(hasMemoryCritical).IsTrue()
            .Because("HealthSweepForTenant must check Memory critical threshold (>= 95)");

        bool hasCpuWarning = sql.Contains("\"CpuUsagePercent\" >= 80", StringComparison.Ordinal);
        bool hasMemoryWarning = sql.Contains("\"MemoryUsagePercent\" >= 80", StringComparison.Ordinal);

        await Assert.That(hasCpuWarning).IsTrue()
            .Because("HealthSweepForTenant must check CPU warning threshold (>= 80)");
        await Assert.That(hasMemoryWarning).IsTrue()
            .Because("HealthSweepForTenant must check Memory warning threshold (>= 80)");
    }

    /// <summary>
    /// Verifies that the HealthSweepForTenant SQL includes an offline check based on LastSeenAt
    /// and uses the tenant partitioning parameter.
    /// </summary>
    [Test]
    public async Task PostgresDialect_HealthSweep_ContainsOfflineAndTenantCheck()
    {
        PostgresSqlDialect dialect = new();
        string sql = dialect.HealthSweepForTenant;

        bool hasLastSeenAtCheck = sql.Contains("\"LastSeenAt\"", StringComparison.Ordinal);
        bool hasOnlineThresholdParam = sql.Contains("@onlineThresholdSeconds", StringComparison.Ordinal);
        bool hasTenantIdParam = sql.Contains("@tenantId", StringComparison.Ordinal);

        await Assert.That(hasLastSeenAtCheck).IsTrue()
            .Because("HealthSweepForTenant must check LastSeenAt for offline detection");
        await Assert.That(hasOnlineThresholdParam).IsTrue()
            .Because("HealthSweepForTenant must use @onlineThresholdSeconds parameter");
        await Assert.That(hasTenantIdParam).IsTrue()
            .Because("HealthSweepForTenant must be scoped to a single tenant");
    }

    /// <summary>
    /// Verifies that the HealthSweepForTenant SQL uses pre-computed scalar columns
    /// (MaxDiskUsagePercent, HasDiskHealthIssue, HasHardwareIssue) instead of JSONB parsing.
    /// </summary>
    [Test]
    public async Task PostgresDialect_HealthSweep_UsesScalarColumnsNotJsonb()
    {
        PostgresSqlDialect dialect = new();
        string sql = dialect.HealthSweepForTenant;

        bool hasMaxDisk = sql.Contains("\"MaxDiskUsagePercent\"", StringComparison.Ordinal);
        bool hasDiskHealth = sql.Contains("\"HasDiskHealthIssue\"", StringComparison.Ordinal);
        bool hasHardwareIssue = sql.Contains("\"HasHardwareIssue\"", StringComparison.Ordinal);
        bool hasNoJsonb = sql.Contains("jsonb_array_elements", StringComparison.Ordinal) == false;

        await Assert.That(hasMaxDisk).IsTrue()
            .Because("HealthSweepForTenant must use pre-computed MaxDiskUsagePercent");
        await Assert.That(hasDiskHealth).IsTrue()
            .Because("HealthSweepForTenant must use pre-computed HasDiskHealthIssue");
        await Assert.That(hasHardwareIssue).IsTrue()
            .Because("HealthSweepForTenant must use pre-computed HasHardwareIssue");
        await Assert.That(hasNoJsonb).IsTrue()
            .Because("HealthSweepForTenant must not use jsonb_array_elements");
    }

    /// <summary>
    /// Verifies that the health sweep only updates rows where HealthStatus actually changed.
    /// </summary>
    [Test]
    public async Task PostgresDialect_HealthSweep_OnlyUpdatesChangedRows()
    {
        PostgresSqlDialect dialect = new();
        string sql = dialect.HealthSweepForTenant;

        bool hasChangeGuard = sql.Contains("\"HealthStatus\" != c.\"NewHealth\"", StringComparison.Ordinal);

        await Assert.That(hasChangeGuard).IsTrue()
            .Because("HealthSweepForTenant must only update rows where HealthStatus changed");
    }
}
