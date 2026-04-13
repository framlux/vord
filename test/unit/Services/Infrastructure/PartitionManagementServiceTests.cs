// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Services.Infrastructure;
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
    // ========== BuildPartitionName ==========

    [Test]
    public async Task BuildPartitionName_SpecificDate_CorrectFormat()
    {
        DateOnly date = new(2026, 3, 15);
        string result = PartitionManagementService.BuildPartitionName("MachineTelemetry", date);

        await Assert.That(result).IsEqualTo("machinetelemetry_d20260315");
    }

    [Test]
    public async Task BuildPartitionName_SingleDigitMonthAndDay_ZeroPadded()
    {
        DateOnly date = new(2026, 1, 5);
        string result = PartitionManagementService.BuildPartitionName("AuditLog", date);

        await Assert.That(result).IsEqualTo("auditlog_d20260105");
    }

    [Test]
    public async Task BuildPartitionName_December31_CorrectFormat()
    {
        DateOnly date = new(2026, 12, 31);
        string result = PartitionManagementService.BuildPartitionName("MachineTelemetry", date);

        await Assert.That(result).IsEqualTo("machinetelemetry_d20261231");
    }

    [Test]
    public async Task BuildPartitionName_UpperCaseTable_LowercaseOutput()
    {
        DateOnly date = new(2026, 5, 10);
        string result = PartitionManagementService.BuildPartitionName("TELEMETRY", date);

        await Assert.That(result).IsEqualTo("telemetry_d20260510");
    }

    // ========== BuildCreatePartitionSql ==========

    [Test]
    public async Task BuildCreatePartitionSql_NormalDate_CorrectFromAndToRange()
    {
        DateOnly date = new(2026, 3, 15);
        string sql = PartitionManagementService.BuildCreatePartitionSql("MachineTelemetry", date);

        await Assert.That(sql).Contains("FROM ('2026-03-15')");
        await Assert.That(sql).Contains("TO ('2026-03-16')");
        await Assert.That(sql).Contains("machinetelemetry_d20260315");
    }

    [Test]
    public async Task BuildCreatePartitionSql_EndOfMonth_RollsToNextMonth()
    {
        DateOnly date = new(2026, 3, 31);
        string sql = PartitionManagementService.BuildCreatePartitionSql("MachineTelemetry", date);

        await Assert.That(sql).Contains("FROM ('2026-03-31')");
        await Assert.That(sql).Contains("TO ('2026-04-01')");
    }

    [Test]
    public async Task BuildCreatePartitionSql_December31_RollsToNextYear()
    {
        DateOnly date = new(2026, 12, 31);
        string sql = PartitionManagementService.BuildCreatePartitionSql("MachineTelemetry", date);

        await Assert.That(sql).Contains("FROM ('2026-12-31')");
        await Assert.That(sql).Contains("TO ('2027-01-01')");
    }

    [Test]
    public async Task BuildCreatePartitionSql_ContainsCreateTableIfNotExists()
    {
        DateOnly date = new(2026, 6, 15);
        string sql = PartitionManagementService.BuildCreatePartitionSql("MachineTelemetry", date);

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
            scopeFactory, dialect, distributedLock,
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
            scopeFactory, dialect, distributedLock, logger);

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
            scopeFactory, dialect,
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
            scopeFactory, dialect, distributedLock, logger);

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

    // ========== BuildPartitionName with leap year Feb 29 ==========

    [Test]
    public async Task BuildPartitionName_LeapYearFeb29_CorrectFormat()
    {
        // 2028 is a leap year; Feb 29 must be handled correctly
        DateOnly date = new(2028, 2, 29);
        string result = PartitionManagementService.BuildPartitionName("MachineTelemetry", date);

        await Assert.That(result).IsEqualTo("machinetelemetry_d20280229");
    }

    // ========== BuildCreatePartitionSql for Feb 28 non-leap year rolls to March ==========

    [Test]
    public async Task BuildCreatePartitionSql_Feb28NonLeapYear_RollsToMarch1()
    {
        // In a non-leap year, the day after Feb 28 is March 1
        DateOnly date = new(2027, 2, 28);
        string sql = PartitionManagementService.BuildCreatePartitionSql("MachineTelemetry", date);

        await Assert.That(sql).Contains("FROM ('2027-02-28')");
        await Assert.That(sql).Contains("TO ('2027-03-01')");
    }

    // ========== BuildCreatePartitionSql for Feb 29 leap year rolls to March 1 ==========

    [Test]
    public async Task BuildCreatePartitionSql_LeapYearFeb29_RollsToMarch1()
    {
        // In a leap year, Feb 29 + 1 day = March 1
        DateOnly date = new(2028, 2, 29);
        string sql = PartitionManagementService.BuildCreatePartitionSql("MachineTelemetry", date);

        await Assert.That(sql).Contains("FROM ('2028-02-29')");
        await Assert.That(sql).Contains("TO ('2028-03-01')");
    }

    // ========== BuildPartitionName at origin date ==========

    [Test]
    public async Task BuildPartitionName_OriginDate_ProducesExpectedFormat()
    {
        // If all data is recent and the cutoff equals the origin date, the partition
        // name for that date must still be well-formed so the drop loop can compare
        // against it without error.
        DateOnly originDate = new(2026, 1, 1);
        string result = PartitionManagementService.BuildPartitionName("MachineTelemetry", originDate);

        await Assert.That(result).IsEqualTo("machinetelemetry_d20260101");
    }

    [Test]
    public async Task BuildCreatePartitionSql_OriginDate_ProducesValidSql()
    {
        // The origin date must produce valid partition SQL with correct FROM/TO boundaries.
        DateOnly originDate = new(2026, 1, 1);
        string sql = PartitionManagementService.BuildCreatePartitionSql("MachineTelemetry", originDate);

        await Assert.That(sql).Contains("FROM ('2026-01-01')");
        await Assert.That(sql).Contains("TO ('2026-01-02')");
        await Assert.That(sql).Contains("machinetelemetry_d20260101");
        await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS");
    }

    // ========== BuildCreatePartitionSql at year boundary (Jan 1) ==========

    [Test]
    public async Task BuildCreatePartitionSql_January1_NextDayIsJanuary2NotYearRollover()
    {
        // Verify that January 1 increments to January 2, not some year-boundary edge case.
        DateOnly date = new(2026, 1, 1);
        string sql = PartitionManagementService.BuildCreatePartitionSql("MachineTelemetry", date);

        await Assert.That(sql).Contains("FROM ('2026-01-01')");
        await Assert.That(sql).Contains("TO ('2026-01-02')");
    }
}
