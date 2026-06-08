// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using Hangfire;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Infrastructure;

/// <summary>
/// Tests for <see cref="PartitionManagementJob"/>. Covers the runtime control flow
/// (SupportsPartitioning gating), the pure static helpers (BuildPartitionName,
/// BuildCreatePartitionSql, PartitionedTableConfig), the internal DropExpiredPartitionsAsync
/// behavior, and constructor null guards.
/// </summary>
public sealed class PartitionManagementJobTests
{
    private static IPartitionRepository RepoFor(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, NullLogger<DatabaseRepository>.Instance);
    }

    // ========== RunAsync control flow ==========

    [Test]
    public async Task RunAsync_SupportsPartitioningFalse_DoesNotTouchRepository()
    {
        // Intent: on SQLite (no partitioning), the job exits before reading retention or attempting
        // any DDL. A mocked repository lets us assert that — the early-return guard is the only
        // line between "schedule fired" and "do nothing"; a regression that drops the guard would
        // be caught here.
        IPartitionRepository repo = Substitute.For<IPartitionRepository>();

        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        dialect.SupportsPartitioning.Returns(false);

        PartitionManagementJob job = new(repo, dialect, Substitute.For<ILogger<PartitionManagementJob>>());

        await job.RunAsync(CancellationToken.None);

        await repo.DidNotReceiveWithAnyArgs().GetMaxRetentionDaysAsync(default);
        await repo.DidNotReceiveWithAnyArgs().ExecutePartitionDdlAsync(default!, default);
    }

    [Test]
    public async Task RunAsync_SupportsPartitioningTrue_AttemptsCreateAndDrop()
    {
        // Intent: pin the exact DDL count emitted by a full run. A regression that silently drops
        // a table from PartitionedTableConfig.Tables, or shrinks the DaysAhead / lookback window,
        // would still pass an "IsGreaterThan(0)" check. The exact math below is the contract.
        //
        // Create-future pass: |Tables| * (DaysAhead + 1) — for each partitioned table the loop
        //   walks offsets 0..DaysAhead inclusive, issuing one CREATE per day.
        // Drop-expired pass:  |Tables| * actualDropDays, where:
        //   cutoff     = today - MaxRetentionDays - DropBufferDays
        //   walkStart  = max(cutoff - (MaxRetentionDays + SafetyBufferDays), PartitionOriginDate)
        //   dropDays   = (cutoff - walkStart).Days  (strict: cursor < cutoff)
        // The walkStart is clamped to PartitionOriginDate (2026-01-01) until the deployment is
        // old enough that the unclamped lookback exceeds the origin distance, at which point the
        // count stabilises at |Tables| * (MaxRetentionDays + SafetyBufferDays). Computing the
        // expected count from the same inputs makes the test deterministic across calendar dates.
        const int MaxRetentionDays = 90;
        const int DaysAhead = 7;
        const int SafetyBufferDays = 7;
        const int DropBufferDays = 2;
        DateOnly partitionOriginDate = new(2026, 1, 1);

        int tableCount = PartitionedTableConfig.Tables.Count;

        DateOnly today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        DateOnly cutoff = today.AddDays(-(MaxRetentionDays + DropBufferDays));
        DateOnly unclampedWalkStart = cutoff.AddDays(-(MaxRetentionDays + SafetyBufferDays));
        DateOnly walkStart = unclampedWalkStart < partitionOriginDate ? partitionOriginDate : unclampedWalkStart;
        int dropDaysPerTable = (cutoff.ToDateTime(TimeOnly.MinValue) - walkStart.ToDateTime(TimeOnly.MinValue)).Days;

        int expectedCreates = tableCount * (DaysAhead + 1);
        int expectedDrops = tableCount * dropDaysPerTable;
        int expectedTotal = expectedCreates + expectedDrops;

        IPartitionRepository repo = Substitute.For<IPartitionRepository>();
        repo.GetMaxRetentionDaysAsync(Arg.Any<CancellationToken>()).Returns((int?)MaxRetentionDays);

        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        dialect.SupportsPartitioning.Returns(true);

        PartitionManagementJob job = new(repo, dialect, Substitute.For<ILogger<PartitionManagementJob>>());

        await job.RunAsync(CancellationToken.None);

        await repo.Received(1).GetMaxRetentionDaysAsync(Arg.Any<CancellationToken>());

        IReadOnlyList<NSubstitute.Core.ICall> ddlCalls = repo.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IPartitionRepository.ExecutePartitionDdlAsync))
            .ToList();
        await Assert.That(ddlCalls.Count).IsEqualTo(expectedTotal);

        int createCalls = ddlCalls.Count(c => ((string)c.GetArguments()[0]!).Contains("CREATE TABLE IF NOT EXISTS"));
        int dropCalls = ddlCalls.Count(c => ((string)c.GetArguments()[0]!).Contains("DROP TABLE IF EXISTS"));
        await Assert.That(createCalls).IsEqualTo(expectedCreates);
        await Assert.That(dropCalls).IsEqualTo(expectedDrops);
    }

    // ========== Constructor null guards ==========

    [Test]
    public async Task Constructor_NullPartitionRepository_Throws()
    {
        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        ILogger<PartitionManagementJob> logger = Substitute.For<ILogger<PartitionManagementJob>>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            PartitionManagementJob _ = new(null!, dialect, logger);

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("partitionRepository");
    }

    [Test]
    public async Task Constructor_NullSqlDialect_Throws()
    {
        IPartitionRepository repo = Substitute.For<IPartitionRepository>();
        ILogger<PartitionManagementJob> logger = Substitute.For<ILogger<PartitionManagementJob>>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            PartitionManagementJob _ = new(repo, null!, logger);

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("sqlDialect");
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        IPartitionRepository repo = Substitute.For<IPartitionRepository>();
        ISqlDialect dialect = Substitute.For<ISqlDialect>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            PartitionManagementJob _ = new(repo, dialect, null!);

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    // ========== BuildPartitionName ==========

    [Test]
    public async Task BuildPartitionName_SpecificDate_CorrectFormat()
    {
        DateOnly date = new(2026, 3, 15);
        string result = PartitionManagementJob.BuildPartitionName("MachineTelemetry", date);

        await Assert.That(result).IsEqualTo("machinetelemetry_d20260315");
    }

    [Test]
    public async Task BuildPartitionName_SingleDigitMonthAndDay_ZeroPadded()
    {
        DateOnly date = new(2026, 1, 5);
        string result = PartitionManagementJob.BuildPartitionName("AuditLog", date);

        await Assert.That(result).IsEqualTo("auditlog_d20260105");
    }

    [Test]
    public async Task BuildPartitionName_December31_CorrectFormat()
    {
        DateOnly date = new(2026, 12, 31);
        string result = PartitionManagementJob.BuildPartitionName("MachineTelemetry", date);

        await Assert.That(result).IsEqualTo("machinetelemetry_d20261231");
    }

    [Test]
    public async Task BuildPartitionName_UpperCaseTable_LowercaseOutput()
    {
        DateOnly date = new(2026, 5, 10);
        string result = PartitionManagementJob.BuildPartitionName("TELEMETRY", date);

        await Assert.That(result).IsEqualTo("telemetry_d20260510");
    }

    [Test]
    public async Task BuildPartitionName_LeapYearFeb29_CorrectFormat()
    {
        DateOnly date = new(2028, 2, 29);
        string result = PartitionManagementJob.BuildPartitionName("MachineTelemetry", date);

        await Assert.That(result).IsEqualTo("machinetelemetry_d20280229");
    }

    [Test]
    public async Task BuildPartitionName_OriginDate_ProducesExpectedFormat()
    {
        DateOnly originDate = new(2026, 1, 1);
        string result = PartitionManagementJob.BuildPartitionName("MachineTelemetry", originDate);

        await Assert.That(result).IsEqualTo("machinetelemetry_d20260101");
    }

    // ========== BuildCreatePartitionSql ==========

    [Test]
    public async Task BuildCreatePartitionSql_NormalDate_CorrectFromAndToRange()
    {
        DateOnly date = new(2026, 3, 15);
        string sql = PartitionManagementJob.BuildCreatePartitionSql("MachineTelemetry", date);

        await Assert.That(sql).Contains("FROM ('2026-03-15')");
        await Assert.That(sql).Contains("TO ('2026-03-16')");
        await Assert.That(sql).Contains("machinetelemetry_d20260315");
    }

    [Test]
    public async Task BuildCreatePartitionSql_EndOfMonth_RollsToNextMonth()
    {
        DateOnly date = new(2026, 3, 31);
        string sql = PartitionManagementJob.BuildCreatePartitionSql("MachineTelemetry", date);

        await Assert.That(sql).Contains("FROM ('2026-03-31')");
        await Assert.That(sql).Contains("TO ('2026-04-01')");
    }

    [Test]
    public async Task BuildCreatePartitionSql_December31_RollsToNextYear()
    {
        DateOnly date = new(2026, 12, 31);
        string sql = PartitionManagementJob.BuildCreatePartitionSql("MachineTelemetry", date);

        await Assert.That(sql).Contains("FROM ('2026-12-31')");
        await Assert.That(sql).Contains("TO ('2027-01-01')");
    }

    [Test]
    public async Task BuildCreatePartitionSql_ContainsCreateTableIfNotExists()
    {
        DateOnly date = new(2026, 6, 15);
        string sql = PartitionManagementJob.BuildCreatePartitionSql("MachineTelemetry", date);

        await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS");
        await Assert.That(sql).Contains("PARTITION OF");
    }

    [Test]
    public async Task BuildCreatePartitionSql_Feb28NonLeapYear_RollsToMarch1()
    {
        DateOnly date = new(2027, 2, 28);
        string sql = PartitionManagementJob.BuildCreatePartitionSql("MachineTelemetry", date);

        await Assert.That(sql).Contains("FROM ('2027-02-28')");
        await Assert.That(sql).Contains("TO ('2027-03-01')");
    }

    [Test]
    public async Task BuildCreatePartitionSql_LeapYearFeb29_RollsToMarch1()
    {
        DateOnly date = new(2028, 2, 29);
        string sql = PartitionManagementJob.BuildCreatePartitionSql("MachineTelemetry", date);

        await Assert.That(sql).Contains("FROM ('2028-02-29')");
        await Assert.That(sql).Contains("TO ('2028-03-01')");
    }

    [Test]
    public async Task BuildCreatePartitionSql_OriginDate_ProducesValidSql()
    {
        DateOnly originDate = new(2026, 1, 1);
        string sql = PartitionManagementJob.BuildCreatePartitionSql("MachineTelemetry", originDate);

        await Assert.That(sql).Contains("FROM ('2026-01-01')");
        await Assert.That(sql).Contains("TO ('2026-01-02')");
        await Assert.That(sql).Contains("machinetelemetry_d20260101");
        await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS");
    }

    [Test]
    public async Task BuildCreatePartitionSql_January1_NextDayIsJanuary2NotYearRollover()
    {
        DateOnly date = new(2026, 1, 1);
        string sql = PartitionManagementJob.BuildCreatePartitionSql("MachineTelemetry", date);

        await Assert.That(sql).Contains("FROM ('2026-01-01')");
        await Assert.That(sql).Contains("TO ('2026-01-02')");
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

    // ========== DropExpiredPartitionsAsync behavioral tests (real SQLite via DatabaseRepository) ==========

    [Test]
    public async Task DropExpiredPartitions_DropsOldPartitions_KeepsRecentOnes()
    {
        // Arrange: seed TierFeatureLimits (1-day retention) and create fake partition tables.
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        await db.InsertAsync(TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Free));
        await db.InsertAsync(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Free,
            MachineLimit = 3,
            RetentionDays = 1,
            AlertRuleLimit = 0,
            WebhookLimit = 0,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        DateOnly today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        int retentionPlusBuffer = 1 + PartitionManagementJob.DropBufferDays;

        // "Old" must remain within the bounded lookback (retention + 7-day safety buffer) so the
        // walk reaches it; that bound is verified separately in the FarPastOrigin test.
        DateOnly oldDate = today.AddDays(-(retentionPlusBuffer + 5));
        DateOnly justOutsideWindow = today.AddDays(-(retentionPlusBuffer + 1));
        DateOnly justInsideWindow = today.AddDays(-retentionPlusBuffer + 1);
        DateOnly todayDate = today;

        string oldPartition = PartitionManagementJob.BuildPartitionName("MachineTelemetry", oldDate);
        string outsidePartition = PartitionManagementJob.BuildPartitionName("MachineTelemetry", justOutsideWindow);
        string insidePartition = PartitionManagementJob.BuildPartitionName("MachineTelemetry", justInsideWindow);
        string todayPartition = PartitionManagementJob.BuildPartitionName("MachineTelemetry", todayDate);

        await db.ExecuteAsync($"CREATE TABLE \"{oldPartition}\" (id INTEGER)", CancellationToken.None);
        await db.ExecuteAsync($"CREATE TABLE \"{outsidePartition}\" (id INTEGER)", CancellationToken.None);
        await db.ExecuteAsync($"CREATE TABLE \"{insidePartition}\" (id INTEGER)", CancellationToken.None);
        await db.ExecuteAsync($"CREATE TABLE \"{todayPartition}\" (id INTEGER)", CancellationToken.None);

        PartitionManagementJob job = new(
            RepoFor(dbFactory),
            Substitute.For<ISqlDialect>(),
            Substitute.For<ILogger<PartitionManagementJob>>());

        await job.DropExpiredPartitionsAsync(CancellationToken.None);

        List<string> survivingTables = (await db.QueryToListAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'machinetelemetry_d%'",
            CancellationToken.None)).ToList();

        await Assert.That(survivingTables).DoesNotContain(oldPartition);
        await Assert.That(survivingTables).DoesNotContain(outsidePartition);
        await Assert.That(survivingTables).Contains(insidePartition);
        await Assert.That(survivingTables).Contains(todayPartition);
    }

    [Test]
    public async Task DropExpiredPartitions_NoSubscriptions_DoesNotDropAnything()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        string partitionName = PartitionManagementJob.BuildPartitionName("MachineTelemetry", new DateOnly(2026, 1, 1));
        await db.ExecuteAsync($"CREATE TABLE \"{partitionName}\" (id INTEGER)", CancellationToken.None);

        PartitionManagementJob job = new(
            RepoFor(dbFactory),
            Substitute.For<ISqlDialect>(),
            Substitute.For<ILogger<PartitionManagementJob>>());

        await job.DropExpiredPartitionsAsync(CancellationToken.None);

        List<string> survivingTables = (await db.QueryToListAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'machinetelemetry_d%'",
            CancellationToken.None)).ToList();

        await Assert.That(survivingTables).Contains(partitionName);
    }

    [Test]
    public async Task DropExpiredPartitions_HigherRetention_KeepsMorePartitions()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        await db.InsertAsync(TestDataBuilder.BuildSubscription(tenantId: 1, tier: SubscriptionTier.Pro));
        await db.InsertAsync(new TierFeatureLimit
        {
            Tier = SubscriptionTier.Pro,
            MachineLimit = 1000,
            RetentionDays = 60,
            AlertRuleLimit = 10,
            WebhookLimit = 5,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        DateOnly today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        int retentionPlusBuffer = 60 + PartitionManagementJob.DropBufferDays;

        DateOnly recentDate = today.AddDays(-20);
        string recentPartition = PartitionManagementJob.BuildPartitionName("MachineTelemetry", recentDate);
        await db.ExecuteAsync($"CREATE TABLE \"{recentPartition}\" (id INTEGER)", CancellationToken.None);

        DateOnly expiredDate = today.AddDays(-(retentionPlusBuffer + 5));
        string expiredPartition = PartitionManagementJob.BuildPartitionName("MachineTelemetry", expiredDate);
        await db.ExecuteAsync($"CREATE TABLE \"{expiredPartition}\" (id INTEGER)", CancellationToken.None);

        PartitionManagementJob job = new(
            RepoFor(dbFactory),
            Substitute.For<ISqlDialect>(),
            Substitute.For<ILogger<PartitionManagementJob>>());

        await job.DropExpiredPartitionsAsync(CancellationToken.None);

        List<string> survivingTables = (await db.QueryToListAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'machinetelemetry_d%'",
            CancellationToken.None)).ToList();

        await Assert.That(survivingTables).Contains(recentPartition);
        await Assert.That(survivingTables).DoesNotContain(expiredPartition);
    }

    [Test]
    public async Task RunAsync_AutomaticRetryAttribute_IsZeroAttempts()
    {
        // Intent: pin Hangfire AutomaticRetry to 0 attempts. The default of 10 would cause
        // duplicate executions on transient failure; this job is not idempotent under retry.
        MethodInfo method = typeof(PartitionManagementJob).GetMethod(nameof(PartitionManagementJob.RunAsync))
            ?? throw new InvalidOperationException("RunAsync not found");
        AutomaticRetryAttribute? attr = method.GetCustomAttribute<AutomaticRetryAttribute>();

        await Assert.That(attr).IsNotNull();
        await Assert.That(attr!.Attempts).IsEqualTo(0);
    }

    [Test]
    public async Task DropExpiredPartitions_FarPastOrigin_DoesNotWalkEntireRange()
    {
        // Intent: scanning every day from PartitionOriginDate (2026-01-01) forever is unbounded
        // and grows linearly with deployment lifetime. The walk must be capped at
        // MaxRetentionDays + 7-day safety buffer so the daily DDL count is constant. Without the
        // bound, after years of operation the job would issue hundreds of no-op DROPs per table
        // per run. This test verifies the DDL call count stays at or below the bound regardless
        // of how far the PartitionOriginDate is from "today".
        const int MaxRetentionDays = 90;
        const int SafetyBufferDays = 7;
        int tableCount = PartitionedTableConfig.Tables.Count;
        int maxDdlCallsPerTable = MaxRetentionDays + SafetyBufferDays + PartitionManagementJob.DropBufferDays;
        int upperBound = maxDdlCallsPerTable * tableCount;

        IPartitionRepository repo = Substitute.For<IPartitionRepository>();
        repo.GetMaxRetentionDaysAsync(Arg.Any<CancellationToken>()).Returns((int?)MaxRetentionDays);

        PartitionManagementJob job = new(
            repo,
            Substitute.For<ISqlDialect>(),
            Substitute.For<ILogger<PartitionManagementJob>>());

        await job.DropExpiredPartitionsAsync(CancellationToken.None);

        IReadOnlyList<NSubstitute.Core.ICall> ddlCalls = repo.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IPartitionRepository.ExecutePartitionDdlAsync))
            .ToList();

        // If the bound is missing the walk would be (today - 2026-01-01).Days * tableCount calls,
        // which is in the thousands by 2026-05 and grows linearly. The bounded walk is at most
        // (retention + safety + drop-buffer) * tableCount calls and is constant per-run.
        await Assert.That(ddlCalls.Count).IsLessThanOrEqualTo(upperBound);
    }

    [Test]
    public async Task CreatePartitions_DdlFailureNotAlreadyExists_LogsAtWarning()
    {
        // Intent: a real DDL failure (disk-full, permissions, lock timeout) must surface in
        // production logs at Warning rather than be silenced at Debug. Only the "already exists"
        // case (Postgres SqlState 42P07) is expected on every run and stays at Debug. Without
        // this distinction a broken cluster could silently stop creating partitions.
        IPartitionRepository repo = Substitute.For<IPartitionRepository>();
        repo.GetMaxRetentionDaysAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        repo.ExecutePartitionDdlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("permission denied"));

        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        dialect.SupportsPartitioning.Returns(true);

        ILogger<PartitionManagementJob> logger = Substitute.For<ILogger<PartitionManagementJob>>();

        PartitionManagementJob job = new(repo, dialect, logger);

        await job.RunAsync(CancellationToken.None);

        // Each failed create should emit a Warning, not Debug. Inspect the recorded Log() calls
        // directly because the ILogger extension methods (LogWarning/LogDebug) compile down to
        // a single Log<TState>(level, ...) call where TState is FormattedLogValues — that generic
        // makes the standard NSubstitute matchers awkward.
        IReadOnlyList<NSubstitute.Core.ICall> logCalls = logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .ToList();

        int warningCount = logCalls.Count(c => (LogLevel)c.GetArguments()[0]! == LogLevel.Warning);
        int debugCount = logCalls.Count(c => (LogLevel)c.GetArguments()[0]! == LogLevel.Debug);

        await Assert.That(warningCount).IsGreaterThan(0);
        await Assert.That(debugCount).IsEqualTo(0);
    }

    [Test]
    public async Task CreatePartitions_DdlFailureAlreadyExists_LogsAtDebug()
    {
        // Intent: the PostgreSQL "duplicate table" error (SqlState 42P07) is expected during
        // normal operation because the create-future loop overlaps with prior runs. It must stay
        // at Debug to keep production logs clean; only genuinely new failures escalate to
        // Warning. This guards the SqlState branch in the catch.
        IPartitionRepository repo = Substitute.For<IPartitionRepository>();
        repo.GetMaxRetentionDaysAsync(Arg.Any<CancellationToken>()).Returns((int?)null);

        // Construct a PostgresException with SqlState 42P07 via reflection. Npgsql 10's
        // PostgresException has internal constructors; using Activator keeps the test isolated
        // from the exact ctor signature.
        PostgresException alreadyExists = (PostgresException)Activator.CreateInstance(
            typeof(PostgresException),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
            binder: null,
            args: new object[] { "relation already exists", "ERROR", "ERROR", "42P07" },
            culture: null)!;

        repo.ExecutePartitionDdlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw alreadyExists);

        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        dialect.SupportsPartitioning.Returns(true);

        ILogger<PartitionManagementJob> logger = Substitute.For<ILogger<PartitionManagementJob>>();

        PartitionManagementJob job = new(repo, dialect, logger);

        await job.RunAsync(CancellationToken.None);

        IReadOnlyList<NSubstitute.Core.ICall> logCalls = logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .ToList();

        int warningCount = logCalls.Count(c => (LogLevel)c.GetArguments()[0]! == LogLevel.Warning);
        int debugCount = logCalls.Count(c => (LogLevel)c.GetArguments()[0]! == LogLevel.Debug);

        // 42P07 must stay at Debug; never escalate to Warning.
        await Assert.That(warningCount).IsEqualTo(0);
        await Assert.That(debugCount).IsGreaterThan(0);
    }

    [Test]
    public async Task RunAsync_DisableConcurrentExecution_TimeoutMatchesContract()
    {
        // Intent: pin the lock timeout. Use CustomAttributeData since DisableConcurrentExecutionAttribute
        // does not expose timeout via a public property.
        MethodInfo method = typeof(PartitionManagementJob).GetMethod(nameof(PartitionManagementJob.RunAsync))
            ?? throw new InvalidOperationException("RunAsync not found");
        CustomAttributeData? attrData = method.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(DisableConcurrentExecutionAttribute));

        await Assert.That(attrData).IsNotNull();
        await Assert.That(attrData!.ConstructorArguments.Count).IsEqualTo(1);
        int timeoutSeconds = (int)attrData.ConstructorArguments[0].Value!;
        await Assert.That(timeoutSeconds).IsEqualTo(1800);
    }

    // ==========================================================================================
    // H5 regression: BuildPartitionName / BuildCreatePartitionSql reject SQL-injection inputs.
    // ==========================================================================================

    [Test]
    public async Task BuildPartitionName_InjectionAttempt_Throws()
    {
        ArgumentException? ex = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            PartitionManagementJob.BuildPartitionName("'); DROP TABLE Tenants; --", new DateOnly(2026, 5, 20));

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task BuildPartitionName_TableWithSpace_Throws()
    {
        ArgumentException? ex = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            PartitionManagementJob.BuildPartitionName("Machine Telemetry", new DateOnly(2026, 5, 20));

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task BuildPartitionName_EmptyTableName_Throws()
    {
        ArgumentException? ex = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            PartitionManagementJob.BuildPartitionName(string.Empty, new DateOnly(2026, 5, 20));

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }

    [Test]
    public async Task BuildCreatePartitionSql_InjectionAttempt_Throws()
    {
        ArgumentException? ex = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            PartitionManagementJob.BuildCreatePartitionSql("\"; DELETE FROM Users; --", new DateOnly(2026, 5, 20));

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
    }
}
