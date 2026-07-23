// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using Framlux.FleetManagement.Database;
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
/// (SupportsPartitioning gating), the pure static helpers (simple and class-qualified name/SQL
/// builders, cutoff/walk math), the internal DropExpiredPartitionsAsync behavior including per-class
/// windows and the Long-only override extension, and constructor null guards.
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
        // line between "schedule fired" and "do nothing".
        IPartitionRepository repo = Substitute.For<IPartitionRepository>();

        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        dialect.SupportsPartitioning.Returns(false);

        PartitionManagementJob job = new(repo, dialect, Substitute.For<ILogger<PartitionManagementJob>>());

        await job.RunAsync(CancellationToken.None);

        await repo.DidNotReceiveWithAnyArgs().GetLongClassRetentionDaysAsync(default);
        await repo.DidNotReceiveWithAnyArgs().ExecutePartitionDdlAsync(default!, default);
    }

    [Test]
    public async Task RunAsync_SupportsPartitioningTrue_CreatesEveryClassAndRangeTablePerDay()
    {
        // Intent: pin the exact DDL count of a full run so a regression that silently drops a
        // retention class or a range table from the config is caught. Creates: one leaf per day for
        // each of the three retention classes plus each simple range table. Drops: computed from the
        // job's own cutoff/walk helpers per class window (Short=1, Medium=60, Long=resolved) and the
        // range tables (Long window), which keeps the expectation deterministic across calendar dates.
        const int LongWindow = 365;
        const int DaysAhead = 7;

        int createSets = PartitionedTableConfig.RangeTables.Count + PartitionedTableConfig.TelemetryRetentionClasses.Count;
        int expectedCreates = createSets * (DaysAhead + 1);

        DateOnly today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        int DropDaysFor(int window)
        {
            DateOnly cutoff = PartitionManagementJob.ComputeDropCutoff(today, window);
            DateOnly walk = PartitionManagementJob.ComputeWalkStart(cutoff, window);

            // The walk is `while (cursor < cutoff)`, so a cutoff at or before the walk start (e.g. the
            // Long window's cutoff lands before the partition origin) yields zero drops, not negative.
            return Math.Max(0, cutoff.DayNumber - walk.DayNumber);
        }

        int expectedDrops =
            DropDaysFor(RetentionClassPolicy.ShortWindowDays)
            + DropDaysFor(RetentionClassPolicy.MediumWindowDays)
            + DropDaysFor(LongWindow)
            + (PartitionedTableConfig.RangeTables.Count * DropDaysFor(LongWindow));
        int expectedTotal = expectedCreates + expectedDrops;

        IPartitionRepository repo = Substitute.For<IPartitionRepository>();
        repo.GetLongClassRetentionDaysAsync(Arg.Any<CancellationToken>()).Returns(LongWindow);

        ISqlDialect dialect = Substitute.For<ISqlDialect>();
        dialect.SupportsPartitioning.Returns(true);

        PartitionManagementJob job = new(repo, dialect, Substitute.For<ILogger<PartitionManagementJob>>());

        await job.RunAsync(CancellationToken.None);

        await repo.Received(1).GetLongClassRetentionDaysAsync(Arg.Any<CancellationToken>());

        IReadOnlyList<NSubstitute.Core.ICall> ddlCalls = repo.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IPartitionRepository.ExecutePartitionDdlAsync))
            .ToList();
        await Assert.That(ddlCalls.Count).IsEqualTo(expectedTotal);

        int createCalls = ddlCalls.Count(c => ((string)c.GetArguments()[0]!).Contains("CREATE TABLE IF NOT EXISTS"));
        int dropCalls = ddlCalls.Count(c => ((string)c.GetArguments()[0]!).Contains("DROP TABLE IF EXISTS"));
        await Assert.That(createCalls).IsEqualTo(expectedCreates);
        await Assert.That(dropCalls).IsEqualTo(expectedDrops);

        // Every retention class parent must appear in a create statement.
        string allCreateSql = string.Join("\n", ddlCalls
            .Select(c => (string)c.GetArguments()[0]!)
            .Where(sql => sql.Contains("CREATE TABLE IF NOT EXISTS")));
        await Assert.That(allCreateSql).Contains(@"PARTITION OF ""MachineTelemetry_Short""");
        await Assert.That(allCreateSql).Contains(@"PARTITION OF ""MachineTelemetry_Medium""");
        await Assert.That(allCreateSql).Contains(@"PARTITION OF ""MachineTelemetry_Long""");
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

    // ========== BuildPartitionName (simple range tables) ==========

    [Test]
    public async Task BuildPartitionName_SpecificDate_CorrectFormat()
    {
        DateOnly date = new(2026, 3, 15);
        string result = PartitionManagementJob.BuildPartitionName("AuditLog", date);

        await Assert.That(result).IsEqualTo("auditlog_d20260315");
    }

    [Test]
    public async Task BuildPartitionName_SingleDigitMonthAndDay_ZeroPadded()
    {
        DateOnly date = new(2026, 1, 5);
        string result = PartitionManagementJob.BuildPartitionName("AuditLog", date);

        await Assert.That(result).IsEqualTo("auditlog_d20260105");
    }

    [Test]
    public async Task BuildPartitionName_LeapYearFeb29_CorrectFormat()
    {
        DateOnly date = new(2028, 2, 29);
        string result = PartitionManagementJob.BuildPartitionName("RemoteCommands", date);

        await Assert.That(result).IsEqualTo("remotecommands_d20280229");
    }

    // ========== BuildCreatePartitionSql (simple range tables) ==========

    [Test]
    public async Task BuildCreatePartitionSql_NormalDate_CorrectFromAndToRange()
    {
        DateOnly date = new(2026, 3, 15);
        string sql = PartitionManagementJob.BuildCreatePartitionSql("AuditLog", date);

        await Assert.That(sql).Contains("FROM ('2026-03-15')");
        await Assert.That(sql).Contains("TO ('2026-03-16')");
        await Assert.That(sql).Contains("auditlog_d20260315");
    }

    [Test]
    public async Task BuildCreatePartitionSql_December31_RollsToNextYear()
    {
        DateOnly date = new(2026, 12, 31);
        string sql = PartitionManagementJob.BuildCreatePartitionSql("AuditLog", date);

        await Assert.That(sql).Contains("FROM ('2026-12-31')");
        await Assert.That(sql).Contains("TO ('2027-01-01')");
    }

    [Test]
    public async Task BuildCreatePartitionSql_ContainsCreateTableIfNotExists()
    {
        DateOnly date = new(2026, 6, 15);
        string sql = PartitionManagementJob.BuildCreatePartitionSql("AlertEvents", date);

        await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS");
        await Assert.That(sql).Contains("PARTITION OF");
    }

    // ========== Class-qualified builders (MachineTelemetry composite) ==========

    [Test]
    [Arguments(RetentionClass.Short, "MachineTelemetry_Short")]
    [Arguments(RetentionClass.Medium, "MachineTelemetry_Medium")]
    [Arguments(RetentionClass.Long, "MachineTelemetry_Long")]
    public async Task ClassParentTableName_MapsClassToParent(RetentionClass retentionClass, string expected)
    {
        string result = PartitionManagementJob.ClassParentTableName(retentionClass);

        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task BuildClassPartitionName_IsClassQualifiedAndDateStamped()
    {
        DateOnly date = new(2026, 7, 22);

        await Assert.That(PartitionManagementJob.BuildClassPartitionName(RetentionClass.Short, date))
            .IsEqualTo("MachineTelemetry_Short_20260722");
        await Assert.That(PartitionManagementJob.BuildClassPartitionName(RetentionClass.Medium, date))
            .IsEqualTo("MachineTelemetry_Medium_20260722");
        await Assert.That(PartitionManagementJob.BuildClassPartitionName(RetentionClass.Long, date))
            .IsEqualTo("MachineTelemetry_Long_20260722");
    }

    [Test]
    public async Task BuildCreateClassPartitionSql_TargetsClassParentWithDayBounds()
    {
        DateOnly date = new(2026, 7, 22);
        string sql = PartitionManagementJob.BuildCreateClassPartitionSql(RetentionClass.Medium, date);

        await Assert.That(sql).Contains("CREATE TABLE IF NOT EXISTS \"MachineTelemetry_Medium_20260722\"");
        await Assert.That(sql).Contains("PARTITION OF \"MachineTelemetry_Medium\"");
        await Assert.That(sql).Contains("FROM ('2026-07-22')");
        await Assert.That(sql).Contains("TO ('2026-07-23')");
    }

    [Test]
    public async Task BuildCreateClassPartitionSql_EndOfYear_RollsToNextYear()
    {
        DateOnly date = new(2026, 12, 31);
        string sql = PartitionManagementJob.BuildCreateClassPartitionSql(RetentionClass.Long, date);

        await Assert.That(sql).Contains("FROM ('2026-12-31')");
        await Assert.That(sql).Contains("TO ('2027-01-01')");
    }

    // ========== ClassWindowDays ==========

    [Test]
    public async Task ClassWindowDays_ShortAndMedium_UseFixedConstants_LongUsesResolvedWindow()
    {
        // The Long window is the only one that can be extended by an override; Short and Medium are
        // fixed forever, so passing a large resolved Long window must not stretch them.
        const int ResolvedLong = 400;

        await Assert.That(PartitionManagementJob.ClassWindowDays(RetentionClass.Short, ResolvedLong))
            .IsEqualTo(RetentionClassPolicy.ShortWindowDays);
        await Assert.That(PartitionManagementJob.ClassWindowDays(RetentionClass.Medium, ResolvedLong))
            .IsEqualTo(RetentionClassPolicy.MediumWindowDays);
        await Assert.That(PartitionManagementJob.ClassWindowDays(RetentionClass.Long, ResolvedLong))
            .IsEqualTo(ResolvedLong);
    }

    // ========== PartitionedTableConfig ==========

    [Test]
    public async Task PartitionedTableConfig_RangeTables_AreTheThreeSimpleTables()
    {
        IReadOnlyList<PartitionedTableConfig.PartitionedTable> tables = PartitionedTableConfig.RangeTables;

        await Assert.That(tables.Count).IsEqualTo(3);
        await Assert.That(tables.Any(t => t.TableName == "AuditLog")).IsTrue();
        await Assert.That(tables.Any(t => t.TableName == "AlertEvents")).IsTrue();
        await Assert.That(tables.Any(t => t.TableName == "RemoteCommands")).IsTrue();

        // MachineTelemetry is composite and must not be in the simple range list.
        await Assert.That(tables.Any(t => t.TableName == "MachineTelemetry")).IsFalse();
    }

    [Test]
    public async Task PartitionedTableConfig_TelemetryRetentionClasses_AreShortMediumLong()
    {
        IReadOnlyList<RetentionClass> classes = PartitionedTableConfig.TelemetryRetentionClasses;

        await Assert.That(classes.Count).IsEqualTo(3);
        await Assert.That(classes).Contains(RetentionClass.Short);
        await Assert.That(classes).Contains(RetentionClass.Medium);
        await Assert.That(classes).Contains(RetentionClass.Long);
    }

    // ========== DropExpiredPartitionsAsync (real SQLite via DatabaseRepository) ==========

    [Test]
    public async Task DropExpiredPartitions_DropsExpiredShortLeaf_ButSurvivesSameDayLongLeaf()
    {
        // Intent: a telemetry day that is expired for the Short class (older than the 1-day window)
        // must be dropped from Short while the SAME day survives in Long, whose window is 365. This is
        // the core promise of per-class partitioning: each class drops on its own schedule.
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        DateOnly today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        // A day comfortably past the Short cutoff but within Short's bounded lookback window.
        DateOnly expiredForShort = PartitionManagementJob.ComputeDropCutoff(today, RetentionClassPolicy.ShortWindowDays).AddDays(-1);

        string shortLeaf = PartitionManagementJob.BuildClassPartitionName(RetentionClass.Short, expiredForShort);
        string longLeaf = PartitionManagementJob.BuildClassPartitionName(RetentionClass.Long, expiredForShort);

        await db.ExecuteAsync($"CREATE TABLE \"{shortLeaf}\" (id INTEGER)", CancellationToken.None);
        await db.ExecuteAsync($"CREATE TABLE \"{longLeaf}\" (id INTEGER)", CancellationToken.None);

        PartitionManagementJob job = new(
            RepoFor(dbFactory),
            Substitute.For<ISqlDialect>(),
            Substitute.For<ILogger<PartitionManagementJob>>());

        await job.DropExpiredPartitionsAsync(CancellationToken.None);

        List<string> surviving = await SurvivingTablesAsync(db);

        await Assert.That(surviving).DoesNotContain(shortLeaf);
        await Assert.That(surviving).Contains(longLeaf);
    }

    [Test]
    public async Task DropExpiredPartitions_OverrideExtendsOnlyLongWindow()
    {
        // Intent: a 400-day retention override extends ONLY the Long class. A Long leaf older than the
        // 365-day floor but within 400 days survives, while Short and Medium leaves past their fixed
        // windows are still dropped — one tenant's override can never stretch another class's window.
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        await db.InsertAsync(new TenantSubscriptionOverride
        {
            TenantId = 1,
            RetentionDays = 400,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        DateOnly today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        DateOnly longSurvivesDay = today.AddDays(-380);   // past 365 floor, within the 400 override
        DateOnly shortExpiredDay = today.AddDays(-5);      // past Short's 1-day window
        DateOnly mediumExpiredDay = today.AddDays(-90);    // past Medium's 60-day window

        string longLeaf = PartitionManagementJob.BuildClassPartitionName(RetentionClass.Long, longSurvivesDay);
        string shortLeaf = PartitionManagementJob.BuildClassPartitionName(RetentionClass.Short, shortExpiredDay);
        string mediumLeaf = PartitionManagementJob.BuildClassPartitionName(RetentionClass.Medium, mediumExpiredDay);

        await db.ExecuteAsync($"CREATE TABLE \"{longLeaf}\" (id INTEGER)", CancellationToken.None);
        await db.ExecuteAsync($"CREATE TABLE \"{shortLeaf}\" (id INTEGER)", CancellationToken.None);
        await db.ExecuteAsync($"CREATE TABLE \"{mediumLeaf}\" (id INTEGER)", CancellationToken.None);

        PartitionManagementJob job = new(
            RepoFor(dbFactory),
            Substitute.For<ISqlDialect>(),
            Substitute.For<ILogger<PartitionManagementJob>>());

        await job.DropExpiredPartitionsAsync(CancellationToken.None);

        List<string> surviving = await SurvivingTablesAsync(db);

        await Assert.That(surviving).Contains(longLeaf);
        await Assert.That(surviving).DoesNotContain(shortLeaf);
        await Assert.That(surviving).DoesNotContain(mediumLeaf);
    }

    [Test]
    public async Task DropExpiredPartitions_FarPastOrigin_DoesNotWalkEntireRange()
    {
        // Intent: the drop walk must be bounded (window + 7-day safety buffer) so the daily DDL count
        // stays constant per run rather than growing with deployment lifetime. Without the bound the
        // Long-class walk would issue thousands of no-op DROPs by mid-2026.
        const int LongWindow = 365;
        int perSetUpperBound = LongWindow + 7 + PartitionManagementJob.DropBufferDays;
        // Three classes (Short/Medium bounded far tighter, but bound by Long is the worst case) plus
        // three range tables at the Long window.
        int setCount = PartitionedTableConfig.RangeTables.Count + PartitionedTableConfig.TelemetryRetentionClasses.Count;
        int upperBound = perSetUpperBound * setCount;

        IPartitionRepository repo = Substitute.For<IPartitionRepository>();
        repo.GetLongClassRetentionDaysAsync(Arg.Any<CancellationToken>()).Returns(LongWindow);

        PartitionManagementJob job = new(
            repo,
            Substitute.For<ISqlDialect>(),
            Substitute.For<ILogger<PartitionManagementJob>>());

        await job.DropExpiredPartitionsAsync(CancellationToken.None);

        IReadOnlyList<NSubstitute.Core.ICall> ddlCalls = repo.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(IPartitionRepository.ExecutePartitionDdlAsync))
            .ToList();

        await Assert.That(ddlCalls.Count).IsLessThanOrEqualTo(upperBound);
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
    public async Task CreatePartitions_DdlFailureNotAlreadyExists_LogsAtWarning()
    {
        // Intent: a real DDL failure (disk-full, permissions, lock timeout) must surface at Warning,
        // not be silenced at Debug. Only the "already exists" case (Postgres 42P07) stays at Debug.
        IPartitionRepository repo = Substitute.For<IPartitionRepository>();
        repo.ExecutePartitionDdlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("permission denied"));

        ILogger<PartitionManagementJob> logger = Substitute.For<ILogger<PartitionManagementJob>>();

        PartitionManagementJob job = new(repo, Substitute.For<ISqlDialect>(), logger);

        // Exercise the create pass directly so drop-path logging does not mix into the counts.
        await job.CreateFuturePartitionsAsync(CancellationToken.None);

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
        // Intent: the PostgreSQL "duplicate table" error (SqlState 42P07) is expected on every run
        // because the create-future loop overlaps prior runs. It must stay at Debug; only genuinely
        // new failures escalate to Warning.
        IPartitionRepository repo = Substitute.For<IPartitionRepository>();

        PostgresException alreadyExists = (PostgresException)Activator.CreateInstance(
            typeof(PostgresException),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public,
            binder: null,
            args: new object[] { "relation already exists", "ERROR", "ERROR", "42P07" },
            culture: null)!;

        repo.ExecutePartitionDdlAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw alreadyExists);

        ILogger<PartitionManagementJob> logger = Substitute.For<ILogger<PartitionManagementJob>>();

        PartitionManagementJob job = new(repo, Substitute.For<ISqlDialect>(), logger);

        // Exercise the create pass directly so drop-path logging does not mix into the counts.
        await job.CreateFuturePartitionsAsync(CancellationToken.None);

        IReadOnlyList<NSubstitute.Core.ICall> logCalls = logger.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log))
            .ToList();

        int warningCount = logCalls.Count(c => (LogLevel)c.GetArguments()[0]! == LogLevel.Warning);
        int debugCount = logCalls.Count(c => (LogLevel)c.GetArguments()[0]! == LogLevel.Debug);

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
    // Identifier-validation guards: name/SQL builders reject SQL-injection inputs.
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

    private static async Task<List<string>> SurvivingTablesAsync(DatabaseContext db)
    {
        return (await db.QueryToListAsync<string>(
            "SELECT name FROM sqlite_master WHERE type='table'",
            CancellationToken.None)).ToList();
    }
}
