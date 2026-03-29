// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="PostgresSqlDialect"/>.
/// </summary>
public sealed class PostgresSqlDialectTests
{
    private readonly PostgresSqlDialect _dialect = new();

    // --- SSH passthrough tests ---

    [Test]
    public async Task BuildUpsertSshSessions_ReturnsNewPayloadUnchanged()
    {
        string newPayload = """{"timestamp":"2026-01-01T00:00:00Z","user":"alice"}""";

        (string _, string sessionsValue) = _dialect.BuildUpsertSshSessions(null, newPayload);

        await Assert.That(sessionsValue).IsEqualTo(newPayload);
    }

    [Test]
    public async Task BuildUpsertSshSessions_NullExistingSessions_ReturnsNewPayload()
    {
        string newPayload = """{"timestamp":"2026-01-01T00:00:00Z"}""";

        (string _, string sessionsValue) = _dialect.BuildUpsertSshSessions(null, newPayload);

        await Assert.That(sessionsValue).IsEqualTo(newPayload);
    }

    [Test]
    public async Task BuildUpsertSshSessions_SqlContainsJsonbOperations()
    {
        (string sql, string _) = _dialect.BuildUpsertSshSessions(null, """{"timestamp":"2026-01-01T00:00:00Z"}""");

        await Assert.That(sql).Contains("jsonb_build_array");
        await Assert.That(sql).Contains("jsonb_array_elements");
        await Assert.That(sql).Contains("jsonb_agg");
    }

    // --- SQL structural integrity tests ---

    [Test]
    public async Task AllUpsertSqlProperties_UseGreatestNotMax()
    {
        List<string> allSql = new()
        {
            _dialect.UpsertSystemInfo, _dialect.UpsertOsVersion, _dialect.UpsertCpuInfo,
            _dialect.UpsertMemoryInfo, _dialect.UpsertDiskInfo, _dialect.UpsertCpuUsage,
            _dialect.UpsertMemoryUsage, _dialect.UpsertDiskUsage, _dialect.UpsertHardwareHealth,
            _dialect.UpsertPackageUpdates, _dialect.UpsertServiceStatus, _dialect.UpsertLastTelemetry
        };

        foreach (string sql in allSql)
        {
            await Assert.That(sql).Contains("GREATEST(");
            await Assert.That(sql).DoesNotContain("MAX(");
        }
    }

    [Test]
    public async Task AllUpsertSqlProperties_UseExcludedUppercase()
    {
        List<string> allSql = new()
        {
            _dialect.UpsertSystemInfo, _dialect.UpsertOsVersion, _dialect.UpsertCpuInfo,
            _dialect.UpsertMemoryInfo, _dialect.UpsertDiskInfo, _dialect.UpsertCpuUsage,
            _dialect.UpsertMemoryUsage, _dialect.UpsertDiskUsage, _dialect.UpsertHardwareHealth,
            _dialect.UpsertPackageUpdates, _dialect.UpsertServiceStatus, _dialect.UpsertLastTelemetry
        };

        foreach (string sql in allSql)
        {
            await Assert.That(sql).Contains("EXCLUDED.");
        }
    }

    [Test]
    public async Task AllUpsertSqlProperties_ContainStaleDataWhereGuard()
    {
        string guard = """WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL""";

        List<string> allSql = new()
        {
            _dialect.UpsertSystemInfo, _dialect.UpsertOsVersion, _dialect.UpsertCpuInfo,
            _dialect.UpsertMemoryInfo, _dialect.UpsertDiskInfo, _dialect.UpsertCpuUsage,
            _dialect.UpsertMemoryUsage, _dialect.UpsertDiskUsage, _dialect.UpsertHardwareHealth,
            _dialect.UpsertPackageUpdates, _dialect.UpsertServiceStatus, _dialect.UpsertLastTelemetry
        };

        foreach (string sql in allSql)
        {
            await Assert.That(sql).Contains(guard);
        }
    }

    [Test]
    public async Task JsonbCasting_PresentInDiskAndHardwareHealthUpserts()
    {
        await Assert.That(_dialect.UpsertDiskInfo).Contains("::jsonb");
        await Assert.That(_dialect.UpsertDiskUsage).Contains("::jsonb");
        await Assert.That(_dialect.UpsertHardwareHealth).Contains("::jsonb");
    }

    [Test]
    public async Task AllSqlProperties_AreNonNullAndNonEmpty()
    {
        List<string> allSql = new()
        {
            _dialect.UpsertSystemInfo, _dialect.UpsertOsVersion, _dialect.UpsertCpuInfo,
            _dialect.UpsertMemoryInfo, _dialect.UpsertDiskInfo, _dialect.UpsertCpuUsage,
            _dialect.UpsertMemoryUsage, _dialect.UpsertDiskUsage, _dialect.UpsertHardwareHealth,
            _dialect.UpsertPackageUpdates, _dialect.UpsertServiceStatus, _dialect.UpsertLastTelemetry
        };

        foreach (string sql in allSql)
        {
            await Assert.That(string.IsNullOrWhiteSpace(sql)).IsFalse();
        }
    }

    [Test]
    public async Task AllUpsertSqlProperties_UseOnConflictWithCorrectKey()
    {
        List<string> allSql = new()
        {
            _dialect.UpsertSystemInfo, _dialect.UpsertOsVersion, _dialect.UpsertCpuInfo,
            _dialect.UpsertMemoryInfo, _dialect.UpsertDiskInfo, _dialect.UpsertCpuUsage,
            _dialect.UpsertMemoryUsage, _dialect.UpsertDiskUsage, _dialect.UpsertHardwareHealth,
            _dialect.UpsertPackageUpdates, _dialect.UpsertServiceStatus, _dialect.UpsertLastTelemetry
        };

        foreach (string sql in allSql)
        {
            await Assert.That(sql).Contains("""ON CONFLICT ("MachineId")""");
        }
    }

    [Test]
    public async Task BuildUpsertSshSessions_SqlContainsLimit50()
    {
        (string sql, string _) = _dialect.BuildUpsertSshSessions(null, """{"timestamp":"2026-01-01T00:00:00Z"}""");

        await Assert.That(sql).Contains("LIMIT 50");
    }

    [Test]
    public async Task BothDialects_ImplementSameProperties()
    {
        SqliteSqlDialect sqlite = new();

        // Verify both dialects produce non-empty SQL for all the same properties
        await Assert.That(string.IsNullOrEmpty(sqlite.UpsertSystemInfo)).IsFalse();
        await Assert.That(string.IsNullOrEmpty(_dialect.UpsertSystemInfo)).IsFalse();
        await Assert.That(string.IsNullOrEmpty(sqlite.UpsertOsVersion)).IsFalse();
        await Assert.That(string.IsNullOrEmpty(_dialect.UpsertOsVersion)).IsFalse();
        await Assert.That(string.IsNullOrEmpty(sqlite.UpsertCpuInfo)).IsFalse();
        await Assert.That(string.IsNullOrEmpty(_dialect.UpsertCpuInfo)).IsFalse();
        await Assert.That(string.IsNullOrEmpty(sqlite.UpsertMemoryInfo)).IsFalse();
        await Assert.That(string.IsNullOrEmpty(_dialect.UpsertMemoryInfo)).IsFalse();
        await Assert.That(string.IsNullOrEmpty(sqlite.UpsertDiskInfo)).IsFalse();
        await Assert.That(string.IsNullOrEmpty(_dialect.UpsertDiskInfo)).IsFalse();
        await Assert.That(string.IsNullOrEmpty(sqlite.UpsertCpuUsage)).IsFalse();
        await Assert.That(string.IsNullOrEmpty(_dialect.UpsertCpuUsage)).IsFalse();
        await Assert.That(string.IsNullOrEmpty(sqlite.UpsertLastTelemetry)).IsFalse();
        await Assert.That(string.IsNullOrEmpty(_dialect.UpsertLastTelemetry)).IsFalse();
    }
}
