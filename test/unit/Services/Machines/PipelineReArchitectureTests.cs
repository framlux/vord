// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Machines;

/// <summary>
/// Tests covering the pipeline re-architecture: stream partitioning, batch coalescing,
/// per-type timestamp guards, and health status pre-computation.
/// </summary>
public class PipelineReArchitectureTests
{
    // -----------------------------------------------------------------------
    // MachineStateQueueService.GetStreamKey — partition consistency
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that the same machine ID always maps to the same stream partition.
    /// </summary>
    [Test]
    public async Task GetStreamKey_PartitionsConsistently()
    {
        long machineId = 42;
        string first = MachineStateQueueService.GetStreamKey(machineId);
        string second = MachineStateQueueService.GetStreamKey(machineId);
        string third = MachineStateQueueService.GetStreamKey(machineId);

        await Assert.That(first).IsEqualTo(second);
        await Assert.That(second).IsEqualTo(third);
    }

    /// <summary>
    /// Verifies that multiple distinct machine IDs spread across different partitions,
    /// confirming at least two distinct keys appear over a reasonable sample.
    /// </summary>
    [Test]
    public async Task GetStreamKey_DifferentMachines_DistributeAcrossPartitions()
    {
        HashSet<string> keys = [];
        for (long id = 0; id < 100; id++)
        {
            keys.Add(MachineStateQueueService.GetStreamKey(id));
        }

        // With 8 partitions, 100 sequential IDs should hit all 8.
        await Assert.That(keys.Count).IsEqualTo(MachineStateQueueService.PartitionCount);

        // Every key should start with the stream prefix.
        foreach (string key in keys)
        {
            await Assert.That(key).StartsWith(MachineStateQueueService.StreamPrefix);
        }
    }

    /// <summary>
    /// Verifies that negative machine IDs do not throw and produce a valid stream key.
    /// </summary>
    [Test]
    public async Task GetStreamKey_NegativeId_DoesNotThrow()
    {
        string key = MachineStateQueueService.GetStreamKey(-7);

        await Assert.That(key).IsNotNull();
        await Assert.That(key).StartsWith(MachineStateQueueService.StreamPrefix);
    }

    // -----------------------------------------------------------------------
    // MachineStateUpdater.UpdateBatchAsync — empty input and coalescing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that an empty dictionary results in no database calls and returns normally.
    /// </summary>
    [Test]
    public async Task UpdateBatchAsync_EmptyDictionary_DoesNothing()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        ILogger<MachineStateUpdater> logger = new NullLogger<MachineStateUpdater>();
        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        IServerSettingsCache settingsCache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        ServerConfigurationService configService = new(settingsCache, redis);

        MachineStateUpdater updater = new(scopeFactory, logger, dialect, configService);

        Dictionary<long, List<StateUpdateMessage>> empty = [];

        // Should complete without creating any scope or calling any SQL.
        await updater.UpdateBatchAsync(empty, CancellationToken.None);

        // No scope was created — confirms early-return for empty input.
        scopeFactory.DidNotReceive().CreateScope();
    }

    /// <summary>
    /// Verifies that when two updates of the same telemetry type arrive for the same machine,
    /// only the latest (by ReceivedAt) is kept after coalescing. The older update is discarded.
    /// </summary>
    [Test]
    public async Task UpdateBatchAsync_CoalescesDuplicateTypes()
    {
        using TestDatabaseFactory dbFactory = new();

        // Seed a machine and its pre-created MachineState row.
        Machine machine = TestDataBuilder.BuildMachine(tenantId: 1);
        machine.Id = await dbFactory.Context.InsertWithInt64IdentityAsync(machine);
        MachineState state = TestDataBuilder.BuildMachineState(machineId: machine.Id);
        await dbFactory.Context.InsertAsync(state);

        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        ILogger<MachineStateUpdater> logger = new NullLogger<MachineStateUpdater>();
        SqliteSqlDialect dialect = new();
        IServerSettingsCache settingsCache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        ServerConfigurationService configService = new(settingsCache, redis);

        MachineStateUpdater updater = new(scopeFactory, logger, dialect, configService);

        DateTimeOffset older = DateTimeOffset.UtcNow.AddMinutes(-5);
        DateTimeOffset newer = DateTimeOffset.UtcNow;

        // Two CpuUsage updates for the same machine; the older one should be discarded.
        Dictionary<long, List<StateUpdateMessage>> updates = new()
        {
            [machine.Id] =
            [
                new StateUpdateMessage
                {
                    TelemetryType = 6, // CpuUsage
                    Payload = """{"cpu_usage_percent":50}""",
                    ReceivedAt = older
                },
                new StateUpdateMessage
                {
                    TelemetryType = 6, // CpuUsage
                    Payload = """{"cpu_usage_percent":90}""",
                    ReceivedAt = newer
                }
            ]
        };

        await updater.UpdateBatchAsync(updates, CancellationToken.None);

        // The persisted CpuUsagePercent should be 90 (from the newer message), not 50.
        MachineState? result = await dbFactory.Context.MachineStates
            .Where(ms => ms.MachineId == machine.Id)
            .FirstOrDefaultAsync();

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.CpuUsagePercent).IsEqualTo(90);
    }

    // -----------------------------------------------------------------------
    // PostgresSqlDialect — per-type timestamp guard correctness
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that no UPSERT (except UpsertLastTelemetry) uses LastTelemetryAt as the WHERE guard.
    /// Each type must guard against its own timestamp column to prevent cross-type interference.
    /// </summary>
    [Test]
    public async Task PostgresDialect_EachUpsertGuardsOwnTimestamp()
    {
        PostgresSqlDialect dialect = new();

        // Collect all type-specific upserts (excluding the fallback UpsertLastTelemetry).
        Dictionary<string, string> upserts = new()
        {
            ["UpsertSystemInfo"] = dialect.UpsertSystemInfo,
            ["UpsertOsVersion"] = dialect.UpsertOsVersion,
            ["UpsertCpuInfo"] = dialect.UpsertCpuInfo,
            ["UpsertMemoryInfo"] = dialect.UpsertMemoryInfo,
            ["UpsertDiskInfo"] = dialect.UpsertDiskInfo,
            ["UpsertCpuUsage"] = dialect.UpsertCpuUsage,
            ["UpsertMemoryUsage"] = dialect.UpsertMemoryUsage,
            ["UpsertDiskUsage"] = dialect.UpsertDiskUsage,
            ["UpsertHardwareHealth"] = dialect.UpsertHardwareHealth,
            ["UpsertPackageUpdates"] = dialect.UpsertPackageUpdates,
            ["UpsertServiceStatus"] = dialect.UpsertServiceStatus,
        };

        foreach (KeyValuePair<string, string> entry in upserts)
        {
            // Extract the WHERE clause portion after DO UPDATE SET.
            string sql = entry.Value;
            int whereIndex = sql.LastIndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
            string whereClause = whereIndex >= 0 ? sql[whereIndex..] : "";

            // The WHERE guard must NOT reference "LastTelemetryAt" — each type guards its own column.
            bool usesLastTelemetryAtAsGuard = whereClause.Contains("\"LastTelemetryAt\"", StringComparison.Ordinal);

            await Assert.That(usesLastTelemetryAtAsGuard).IsFalse()
                .Because($"{entry.Key} must guard its own timestamp, not LastTelemetryAt");
        }
    }

    /// <summary>
    /// Verifies that every UPSERT advances LastTelemetryAt via GREATEST() so the aggregate
    /// timestamp always reflects the most recent telemetry of any type.
    /// </summary>
    [Test]
    public async Task PostgresDialect_AllUpsertsStillAdvanceLastTelemetryAt()
    {
        PostgresSqlDialect dialect = new();

        List<string> allUpserts =
        [
            dialect.UpsertSystemInfo,
            dialect.UpsertOsVersion,
            dialect.UpsertCpuInfo,
            dialect.UpsertMemoryInfo,
            dialect.UpsertDiskInfo,
            dialect.UpsertCpuUsage,
            dialect.UpsertMemoryUsage,
            dialect.UpsertDiskUsage,
            dialect.UpsertHardwareHealth,
            dialect.UpsertPackageUpdates,
            dialect.UpsertServiceStatus,
            dialect.UpsertLastTelemetry,
        ];

        foreach (string sql in allUpserts)
        {
            bool advancesTimestamp = sql.Contains("GREATEST(\"MachineState\".\"LastTelemetryAt\"", StringComparison.Ordinal);

            await Assert.That(advancesTimestamp).IsTrue()
                .Because("every UPSERT must advance LastTelemetryAt via GREATEST()");
        }
    }

    // -----------------------------------------------------------------------
    // PostgresSqlDialect — HealthStatus pre-computation SQL correctness
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that the RecomputeHealthStatus SQL contains CASE branches for both critical
    /// (>= 95) and warning (>= 80) thresholds on scalar metrics.
    /// </summary>
    [Test]
    public async Task PostgresDialect_RecomputeHealthStatus_ContainsCriticalAndWarningChecks()
    {
        PostgresSqlDialect dialect = new();
        string sql = dialect.RecomputeHealthStatus;

        // Critical thresholds (status = 2)
        bool hasCpuCritical = sql.Contains("\"CpuUsagePercent\" >= 95", StringComparison.Ordinal);
        bool hasMemoryCritical = sql.Contains("\"MemoryUsagePercent\" >= 95", StringComparison.Ordinal);

        await Assert.That(hasCpuCritical).IsTrue()
            .Because("RecomputeHealthStatus must check CPU critical threshold (>= 95)");
        await Assert.That(hasMemoryCritical).IsTrue()
            .Because("RecomputeHealthStatus must check Memory critical threshold (>= 95)");

        // Warning thresholds (status = 1)
        bool hasCpuWarning = sql.Contains("\"CpuUsagePercent\" >= 80", StringComparison.Ordinal);
        bool hasMemoryWarning = sql.Contains("\"MemoryUsagePercent\" >= 80", StringComparison.Ordinal);

        await Assert.That(hasCpuWarning).IsTrue()
            .Because("RecomputeHealthStatus must check CPU warning threshold (>= 80)");
        await Assert.That(hasMemoryWarning).IsTrue()
            .Because("RecomputeHealthStatus must check Memory warning threshold (>= 80)");
    }

    /// <summary>
    /// Verifies that the RecomputeHealthStatus SQL includes an offline check based on LastPingAt.
    /// </summary>
    [Test]
    public async Task PostgresDialect_RecomputeHealthStatus_ContainsOfflineCheck()
    {
        PostgresSqlDialect dialect = new();
        string sql = dialect.RecomputeHealthStatus;

        bool hasLastPingAtCheck = sql.Contains("\"LastPingAt\"", StringComparison.Ordinal);
        bool hasOnlineThresholdParam = sql.Contains("@onlineThresholdSeconds", StringComparison.Ordinal);

        await Assert.That(hasLastPingAtCheck).IsTrue()
            .Because("RecomputeHealthStatus must check LastPingAt for offline detection");
        await Assert.That(hasOnlineThresholdParam).IsTrue()
            .Because("RecomputeHealthStatus must use @onlineThresholdSeconds parameter");
    }

    // -----------------------------------------------------------------------
    // MachineStateQueueService.PublishAsync — Redis stream publish behaviour
    // -----------------------------------------------------------------------

    private static (MachineStateQueueService service, IDatabase redisDb) CreateQueueService()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase().Returns(redisDb);
        NullLogger<MachineStateQueueService> logger = new();
        MachineStateQueueService service = new(redis, logger);

        return (service, redisDb);
    }

    /// <summary>
    /// Verifies that passing an empty items list causes PublishAsync to return immediately
    /// without interacting with Redis at all.
    /// </summary>
    [Test]
    public async Task PublishAsync_EmptyItemsList_DoesNotCallRedis()
    {
        (MachineStateQueueService service, IDatabase redisDb) = CreateQueueService();

        await service.PublishAsync(1, [], CancellationToken.None);

        await redisDb.DidNotReceiveWithAnyArgs().StreamAddAsync(
            default, Array.Empty<NameValueEntry>(), default, default, default, default);
    }

    /// <summary>
    /// Verifies that a non-empty items list causes StreamAddAsync to be called exactly once,
    /// using the correct partition stream key derived from the machine ID.
    /// </summary>
    [Test]
    public async Task PublishAsync_NonEmptyItems_CallsStreamAddAsyncOnCorrectPartition()
    {
        (MachineStateQueueService service, IDatabase redisDb) = CreateQueueService();

        redisDb.StreamAddAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<NameValueEntry[]>(),
            Arg.Any<RedisValue?>(),
            Arg.Any<int?>(),
            Arg.Any<bool>(),
            Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisValue.Null));

        long machineId = 10L;
        List<StateUpdateMessage> items =
        [
            new StateUpdateMessage
            {
                TelemetryType = 1,
                Payload = """{"cpu_usage_percent":42}""",
                ReceivedAt = DateTimeOffset.UtcNow
            }
        ];

        string expectedKey = MachineStateQueueService.GetStreamKey(machineId);

        await service.PublishAsync(machineId, items, CancellationToken.None);

        await redisDb.Received(1).StreamAddAsync(
            Arg.Is<RedisKey>(k => k.ToString() == expectedKey),
            Arg.Any<NameValueEntry[]>(),
            Arg.Any<RedisValue?>(),
            Arg.Any<int?>(),
            Arg.Any<bool>(),
            Arg.Any<CommandFlags>());
    }

    /// <summary>
    /// Verifies that the NameValueEntry array passed to StreamAddAsync contains both the
    /// expected <c>machine_id</c> field and an <c>items</c> field whose value is valid JSON
    /// that round-trips the original telemetry type.
    /// </summary>
    [Test]
    public async Task PublishAsync_SerializedEntryContainsCorrectFields()
    {
        (MachineStateQueueService service, IDatabase redisDb) = CreateQueueService();

        NameValueEntry[]? capturedFields = null;

        redisDb.StreamAddAsync(
            Arg.Any<RedisKey>(),
            Arg.Do<NameValueEntry[]>(f => capturedFields = f),
            Arg.Any<RedisValue?>(),
            Arg.Any<int?>(),
            Arg.Any<bool>(),
            Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisValue.Null));

        long machineId = 7L;
        short telemetryType = 3;
        List<StateUpdateMessage> items =
        [
            new StateUpdateMessage
            {
                TelemetryType = telemetryType,
                Payload = """{"disk_usage_percent":55}""",
                ReceivedAt = DateTimeOffset.UtcNow
            }
        ];

        await service.PublishAsync(machineId, items, CancellationToken.None);

        await Assert.That(capturedFields).IsNotNull();

        // Locate the machine_id and items fields by name.
        NameValueEntry machineIdEntry = Array.Find(capturedFields!, e => e.Name == "machine_id");
        NameValueEntry itemsEntry = Array.Find(capturedFields!, e => e.Name == "items");

        await Assert.That((string?)machineIdEntry.Name).IsEqualTo("machine_id");
        await Assert.That((string?)machineIdEntry.Value).IsEqualTo(machineId.ToString());

        await Assert.That((string?)itemsEntry.Name).IsEqualTo("items");

        // Deserialize the items JSON to confirm it contains the original telemetry type.
        string itemsJson = (string?)itemsEntry.Value ?? string.Empty;
        List<StateUpdateMessage>? deserialized = JsonSerializer.Deserialize<List<StateUpdateMessage>>(
            itemsJson, JsonDefaults.SnakeCase);

        await Assert.That(deserialized).IsNotNull();
        await Assert.That(deserialized!.Count).IsEqualTo(1);
        await Assert.That(deserialized[0].TelemetryType).IsEqualTo(telemetryType);
    }
}
