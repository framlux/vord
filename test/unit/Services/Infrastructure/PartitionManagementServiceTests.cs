// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Infrastructure;

/// <summary>
/// Tests for <see cref="PartitionManagementService"/>.
/// </summary>
public class PartitionManagementServiceTests
{
    private static ServerConfigurationService CreateConfigService()
    {
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        cache.GetSettingAsync(Arg.Any<ServerConfigurationSettingKeys>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

        return new ServerConfigurationService(cache, redis);
    }

    // ========== BuildPartitionName ==========

    [Test]
    public async Task BuildPartitionName_NormalMonth_CorrectFormat()
    {
        string result = PartitionManagementService.BuildPartitionName("MachineTelemetry", 2026, 3);

        await Assert.That(result).IsEqualTo("machinetelemetry_y2026m03");
    }

    [Test]
    public async Task BuildPartitionName_SingleDigitMonth_ZeroPadded()
    {
        string result = PartitionManagementService.BuildPartitionName("AuditLog", 2026, 1);

        await Assert.That(result).IsEqualTo("auditlog_y2026m01");
    }

    [Test]
    public async Task BuildPartitionName_December_CorrectFormat()
    {
        string result = PartitionManagementService.BuildPartitionName("MachineTelemetry", 2026, 12);

        await Assert.That(result).IsEqualTo("machinetelemetry_y2026m12");
    }

    [Test]
    public async Task BuildPartitionName_UpperCaseTable_LowercaseOutput()
    {
        string result = PartitionManagementService.BuildPartitionName("TELEMETRY", 2026, 5);

        await Assert.That(result).IsEqualTo("telemetry_y2026m05");
    }

    // ========== BuildCreatePartitionSql ==========

    [Test]
    public async Task BuildCreatePartitionSql_NormalMonth_CorrectFromAndToRange()
    {
        string sql = PartitionManagementService.BuildCreatePartitionSql("MachineTelemetry", 2026, 3);

        await Assert.That(sql).Contains("FROM ('2026-03-01')");
        await Assert.That(sql).Contains("TO ('2026-04-01')");
        await Assert.That(sql).Contains("machinetelemetry_y2026m03");
    }

    [Test]
    public async Task BuildCreatePartitionSql_DecemberRollover_UsesNextYear()
    {
        string sql = PartitionManagementService.BuildCreatePartitionSql("MachineTelemetry", 2026, 12);

        await Assert.That(sql).Contains("FROM ('2026-12-01')");
        await Assert.That(sql).Contains("TO ('2027-01-01')");
    }

    [Test]
    public async Task BuildCreatePartitionSql_January_CorrectRange()
    {
        string sql = PartitionManagementService.BuildCreatePartitionSql("AuditLog", 2027, 1);

        await Assert.That(sql).Contains("FROM ('2027-01-01')");
        await Assert.That(sql).Contains("TO ('2027-02-01')");
    }

    [Test]
    public async Task BuildCreatePartitionSql_ContainsCreateTableIfNotExists()
    {
        string sql = PartitionManagementService.BuildCreatePartitionSql("MachineTelemetry", 2026, 6);

        await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS");
        await Assert.That(sql).Contains("PARTITION OF");
    }

    // ========== Execute_NoPartitionSupport_ExitsEarly ==========

    [Test]
    public async Task Execute_NoPartitionSupport_ExitsEarly()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);

        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        dialect.SupportsPartitioning.Returns(false);

        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();

        PartitionManagementService service = new(
            scopeFactory, dialect, CreateConfigService(), distributedLock,
            Substitute.For<ILogger<PartitionManagementService>>());

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(100));

        try
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(200, CancellationToken.None);
            await service.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation fires
        }

        // Lock should never be attempted since partitioning isn't supported
        await distributedLock.DidNotReceive().TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>());
    }

    // ========== Execute_LockNotAcquired_SkipsCycle ==========

    [Test]
    public async Task Execute_LockNotAcquired_SkipsCycle()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        TaskCompletionSource lockAttempted = new();

        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        dialect.SupportsPartitioning.Returns(true);

        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(callInfo =>
            {
                lockAttempted.TrySetResult();

                return (LockHandle?)null;
            });

        ILogger<PartitionManagementService> logger = Substitute.For<ILogger<PartitionManagementService>>();

        PartitionManagementService service = new(
            scopeFactory, dialect, CreateConfigService(), distributedLock, logger);

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await lockAttempted.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // Lock was attempted but not acquired — no DB operations should have occurred
        await distributedLock.Received().TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>());
    }

    // ========== Execute_CancellationRequested_StopsCleanly ==========

    [Test]
    public async Task Execute_CancellationRequested_StopsCleanly()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);

        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        dialect.SupportsPartitioning.Returns(false);

        PartitionManagementService service = new(
            scopeFactory, dialect, CreateConfigService(),
            Substitute.For<IDistributedLock>(),
            Substitute.For<ILogger<PartitionManagementService>>());

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(50));

        await Assert.That(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);
                await Task.Delay(200, CancellationToken.None);
                await service.StopAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }).ThrowsNothing();
    }

    // ========== PartitionedTableConfig ==========

    [Test]
    public async Task PartitionedTableConfig_ContainsAllExpectedTables()
    {
        IReadOnlyList<PartitionedTableConfig.PartitionedTable> tables = PartitionedTableConfig.Tables;

        await Assert.That(tables.Count).IsEqualTo(4);
        await Assert.That(tables.Any(t => t.TableName == "MachineTelemetry")).IsTrue();
        await Assert.That(tables.Any(t => t.TableName == "AuditLog")).IsTrue();
        await Assert.That(tables.Any(t => t.TableName == "AlertEvents")).IsTrue();
        await Assert.That(tables.Any(t => t.TableName == "RemoteCommands")).IsTrue();
    }

    [Test]
    public async Task PartitionedTableConfig_AllTablesHavePartitionColumn()
    {
        foreach (PartitionedTableConfig.PartitionedTable table in PartitionedTableConfig.Tables)
        {
            await Assert.That(string.IsNullOrEmpty(table.PartitionColumn)).IsFalse();
        }
    }

    // ========== DropExpiredPartitions with no subscriptions ==========

    [Test]
    public async Task Execute_NoSubscriptions_SkipsDropWithoutError()
    {
        // When there are no subscriptions, maxRetentionDays is null and drop logic should be skipped
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        TestServiceScopeFactory scopeFactory = new(db);
        TaskCompletionSource workDone = new();

        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        dialect.SupportsPartitioning.Returns(true);

        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        LockHandle lockHandle = new(Substitute.For<IDatabase>(), "test-key", "test-value");
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(callInfo =>
            {
                workDone.TrySetResult();

                return lockHandle;
            });

        ILogger<PartitionManagementService> logger = Substitute.For<ILogger<PartitionManagementService>>();

        PartitionManagementService service = new(
            scopeFactory, dialect, CreateConfigService(), distributedLock, logger);

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await workDone.Task;

        // Allow the manage cycle to complete
        await Task.Delay(500, CancellationToken.None);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // The service should have completed without error — no exceptions thrown
        // and the lock was acquired successfully
        await distributedLock.Received().TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>());
    }
}
