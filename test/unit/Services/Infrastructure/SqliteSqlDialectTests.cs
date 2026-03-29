// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Infrastructure;
using System.Text.Json;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="SqliteSqlDialect"/>.
/// </summary>
public sealed class SqliteSqlDialectTests
{
    private readonly SqliteSqlDialect _dialect = new();

    // --- SSH session merging tests ---

    [Test]
    public async Task BuildUpsertSshSessions_MergesExistingAndNewSessions()
    {
        string existing = """[{"timestamp":"2026-01-01T00:00:00Z","user":"alice"}]""";
        string newPayload = """{"timestamp":"2026-01-02T00:00:00Z","user":"bob"}""";

        (string _, string sessionsValue) = _dialect.BuildUpsertSshSessions(existing, newPayload);

        using JsonDocument doc = JsonDocument.Parse(sessionsValue);
        int count = doc.RootElement.GetArrayLength();

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task BuildUpsertSshSessions_SortsByTimestampDescending()
    {
        string existing = """[{"timestamp":"2026-01-01T00:00:00Z","user":"early"}]""";
        string newPayload = """{"timestamp":"2026-01-03T00:00:00Z","user":"late"}""";

        (string _, string sessionsValue) = _dialect.BuildUpsertSshSessions(existing, newPayload);

        using JsonDocument doc = JsonDocument.Parse(sessionsValue);
        string? firstUser = doc.RootElement[0].GetProperty("user").GetString();

        await Assert.That(firstUser).IsEqualTo("late");
    }

    [Test]
    public async Task BuildUpsertSshSessions_CapsAtFiftyEntries()
    {
        // Build 50 existing sessions
        List<object> existingSessions = new();
        for (int i = 0; i < 50; i++)
        {
            existingSessions.Add(new { timestamp = $"2026-01-{(i + 1):D2}T00:00:00Z", user = $"user{i}" });
        }

        string existing = JsonSerializer.Serialize(existingSessions);
        string newPayload = """{"timestamp":"2026-02-01T00:00:00Z","user":"newest"}""";

        (string _, string sessionsValue) = _dialect.BuildUpsertSshSessions(existing, newPayload);

        using JsonDocument doc = JsonDocument.Parse(sessionsValue);
        int count = doc.RootElement.GetArrayLength();

        await Assert.That(count).IsEqualTo(50);
    }

    [Test]
    public async Task BuildUpsertSshSessions_NullExistingSessions_ReturnsOnlyNewSession()
    {
        string newPayload = """{"timestamp":"2026-01-01T00:00:00Z","user":"alice"}""";

        (string _, string sessionsValue) = _dialect.BuildUpsertSshSessions(null, newPayload);

        using JsonDocument doc = JsonDocument.Parse(sessionsValue);

        await Assert.That(doc.RootElement.GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task BuildUpsertSshSessions_EmptyStringExistingSessions_ReturnsOnlyNewSession()
    {
        string newPayload = """{"timestamp":"2026-01-01T00:00:00Z","user":"alice"}""";

        (string _, string sessionsValue) = _dialect.BuildUpsertSshSessions("", newPayload);

        using JsonDocument doc = JsonDocument.Parse(sessionsValue);

        await Assert.That(doc.RootElement.GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task BuildUpsertSshSessions_EmptyArrayExistingSessions_ReturnsOnlyNewSession()
    {
        string newPayload = """{"timestamp":"2026-01-01T00:00:00Z","user":"alice"}""";

        (string _, string sessionsValue) = _dialect.BuildUpsertSshSessions("[]", newPayload);

        using JsonDocument doc = JsonDocument.Parse(sessionsValue);

        await Assert.That(doc.RootElement.GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task BuildUpsertSshSessions_MalformedExistingJson_ReturnsOnlyNewSession()
    {
        string newPayload = """{"timestamp":"2026-01-01T00:00:00Z","user":"alice"}""";

        (string _, string sessionsValue) = _dialect.BuildUpsertSshSessions("not-valid-json{{{", newPayload);

        using JsonDocument doc = JsonDocument.Parse(sessionsValue);

        await Assert.That(doc.RootElement.GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task BuildUpsertSshSessions_MalformedNewPayload_ReturnsExistingSessionsOnly()
    {
        string existing = """[{"timestamp":"2026-01-01T00:00:00Z","user":"alice"}]""";

        (string _, string sessionsValue) = _dialect.BuildUpsertSshSessions(existing, "not-valid-json{{{");

        using JsonDocument doc = JsonDocument.Parse(sessionsValue);

        await Assert.That(doc.RootElement.GetArrayLength()).IsEqualTo(1);
        await Assert.That(doc.RootElement[0].GetProperty("user").GetString()).IsEqualTo("alice");
    }

    [Test]
    public async Task BuildUpsertSshSessions_NonArrayExistingJson_TreatedAsEmpty()
    {
        string newPayload = """{"timestamp":"2026-01-01T00:00:00Z","user":"alice"}""";

        (string _, string sessionsValue) = _dialect.BuildUpsertSshSessions("""{"foo":"bar"}""", newPayload);

        using JsonDocument doc = JsonDocument.Parse(sessionsValue);

        await Assert.That(doc.RootElement.GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task BuildUpsertSshSessions_SessionsWithoutTimestamp_SortToEnd()
    {
        string existing = """[{"user":"no-timestamp"},{"timestamp":"2026-01-01T00:00:00Z","user":"has-timestamp"}]""";
        string newPayload = """{"timestamp":"2026-01-02T00:00:00Z","user":"newest"}""";

        (string _, string sessionsValue) = _dialect.BuildUpsertSshSessions(existing, newPayload);

        using JsonDocument doc = JsonDocument.Parse(sessionsValue);
        string? lastUser = doc.RootElement[doc.RootElement.GetArrayLength() - 1].GetProperty("user").GetString();

        await Assert.That(lastUser).IsEqualTo("no-timestamp");
    }

    // --- SQL structural integrity tests ---

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
    public async Task AllUpsertSqlProperties_UseMaxNotGreatest()
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
            await Assert.That(sql).Contains("MAX(");
            await Assert.That(sql).DoesNotContain("GREATEST(");
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
    public async Task BuildUpsertSshSessions_SqlAlsoContainsStaleDataGuard()
    {
        (string sql, string _) = _dialect.BuildUpsertSshSessions(null, """{"timestamp":"2026-01-01T00:00:00Z"}""");

        string guard = """WHERE @ts >= "MachineState"."LastTelemetryAt" OR "MachineState"."LastTelemetryAt" IS NULL""";

        await Assert.That(sql).Contains(guard);
    }
}
