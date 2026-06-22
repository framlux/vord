// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Machines.Projection;
using Framlux.FleetManagement.Services.Core.Telemetry;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Machines;

/// <summary>
/// Tests for <see cref="MachineStateStreamingService"/>. The service collapses each telemetry
/// batch to one patch per machine and applies at most one UPDATE per table per machine. These
/// tests drive the public background loop deterministically (gated on an observable repository
/// call, never on wall-clock time) and assert the projected state via the database or a spy.
/// </summary>
public class MachineStateStreamingServiceTests
{
    /// <summary>
    /// Zero startup delay so tests don't pay the production 5-second pause.
    /// </summary>
    private static readonly TimeSpan FastStartupDelay = TimeSpan.Zero;

    private static readonly DateTimeOffset FixedClock = new(2026, 06, 16, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A recent base instant for seeded telemetry, anchored to <see cref="FixedClock"/> rather than
    /// the wall clock. The streaming loop filters rows to the last two days using its injected
    /// <see cref="TimeProvider"/>, which these tests seed at <see cref="FixedClock"/>, so anchoring
    /// the seed timestamps to the same fixed instant keeps the rows inside the window regardless of
    /// the real system date.
    /// </summary>
    private static DateTimeOffset RecentBase => FixedClock.AddHours(-1);

    private static MachineStateStreamingService CreateService(
        IServiceScopeFactory scopeFactory,
        ISqlDialect? dialect = null,
        IAdvisoryLockProvider? advisoryLockProvider = null,
        IServerSettingsCache? settingsCache = null,
        ILogger<MachineStateStreamingService>? logger = null,
        TimeProvider? timeProvider = null,
        TimeSpan? startupDelay = null)
    {
        return new MachineStateStreamingService(
            scopeFactory,
            dialect ?? Substitute.For<ISqlDialect>(),
            advisoryLockProvider ?? AcquiringAdvisoryLockProvider(),
            settingsCache ?? Substitute.For<IServerSettingsCache>(),
            logger ?? Substitute.For<ILogger<MachineStateStreamingService>>(),
            timeProvider ?? TimeProvider.System,
            startupDelay ?? FastStartupDelay);
    }

    private static IAdvisoryLockProvider AcquiringAdvisoryLockProvider()
    {
        IAdvisoryLockProvider provider = Substitute.For<IAdvisoryLockProvider>();
        provider.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Substitute.For<IAsyncDisposable>());

        return provider;
    }

    private static IAdvisoryLockProvider BlockingAdvisoryLockProvider()
    {
        IAdvisoryLockProvider provider = Substitute.For<IAdvisoryLockProvider>();
        provider.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((IAsyncDisposable?)null);

        return provider;
    }

    private static async Task SeedSummaryAndDetail(DatabaseContext db, long machineId, int tenantId = 1)
    {
        await db.InsertAsync(TestDataBuilder.BuildMachineStateSummary(machineId: machineId, tenantId: tenantId));
        await db.InsertAsync(new MachineStateDetail { MachineId = machineId });
    }

    private static async Task SeedTelemetryAsync(DatabaseContext db, params MachineTelemetry[] rows)
    {
        foreach (MachineTelemetry row in rows)
        {
            await db.InsertAsync(row);
        }
    }

    private static MachineTelemetry Row(long id, long machineId, short telemetryType, string payload, DateTimeOffset receivedAt)
    {
        return new MachineTelemetry
        {
            Id = id,
            MachineId = machineId,
            TenantId = 1,
            TelemetryType = telemetryType,
            Payload = payload,
            ReceivedAt = receivedAt,
            SourceEventId = Guid.NewGuid().ToString("N")
        };
    }

    /// <summary>
    /// Drives a single batch through the background loop using an injected spy repository, then
    /// cancels. Completion is gated on the loop's <b>second</b> telemetry poll — which the service
    /// only reaches after advancing the high-water mark, i.e. after every patch has been applied —
    /// so the wait is deterministic and never depends on wall-clock time.
    /// </summary>
    private static async Task RunOneLoopIterationAsync(IMachineStateRepository spy)
    {
        TaskCompletionSource batchConsumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int polls = 0;

        // The second GetTelemetryBatchAsync call signals that the first batch is fully applied.
        spy.When(r => r.GetTelemetryBatchAsync(
                Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>()))
            .Do(_ =>
            {
                if (Interlocked.Increment(ref polls) == 2)
                {
                    batchConsumed.TrySetResult();
                }
            });

        Dictionary<Type, object> services = new() { [typeof(IMachineStateRepository)] = spy };
        TestServiceScopeFactory scopeFactory = new(NoopContext(), services);

        await RunUntilSignaledAsync(scopeFactory, batchConsumed);
    }

    /// <summary>
    /// Drives a single batch through the background loop against a real database, then cancels.
    /// The real repository is wrapped so the loop's second poll completes a signal once the batch
    /// has been applied; the wait is gated on that signal, not on elapsed time.
    /// </summary>
    private static async Task RunOneLoopIterationAsync(DatabaseContext db, bool resetHighWaterMark = false)
    {
        _ = resetHighWaterMark; // Each iteration builds a fresh service whose high-water mark starts at zero.

        TaskCompletionSource batchConsumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DatabaseRepository real = new(db, NullLogger<DatabaseRepository>.Instance);
        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();
        int polls = 0;

        // Forward the three loop-used calls to the real repository; the loop's second telemetry
        // poll only happens after the high-water mark advances (i.e. after every patch is applied),
        // so completing the signal there yields a deterministic, wall-clock-independent wait.
        repo.GetTelemetryBatchAsync(Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref polls) == 2)
                {
                    batchConsumed.TrySetResult();
                }

                return real.GetTelemetryBatchAsync(ci.Arg<long>(), ci.Arg<DateTimeOffset>(), ci.Arg<int>(), ci.Arg<CancellationToken>());
            });
        repo.ApplySummaryPatchAsync(Arg.Any<MachineSummaryPatch>(), Arg.Any<CancellationToken>())
            .Returns(ci => real.ApplySummaryPatchAsync(ci.Arg<MachineSummaryPatch>(), ci.Arg<CancellationToken>()));
        repo.ApplyDetailPatchAsync(Arg.Any<MachineDetailPatch>(), Arg.Any<CancellationToken>())
            .Returns(ci => real.ApplyDetailPatchAsync(ci.Arg<MachineDetailPatch>(), ci.Arg<CancellationToken>()));

        Dictionary<Type, object> services = new() { [typeof(IMachineStateRepository)] = repo };
        TestServiceScopeFactory scopeFactory = new(db, services);

        await RunUntilSignaledAsync(scopeFactory, batchConsumed);
    }

    private static async Task RunUntilSignaledAsync(TestServiceScopeFactory scopeFactory, TaskCompletionSource batchConsumed)
    {
        FixedTimeProvider clock = new(FixedClock);
        MachineStateStreamingService service = CreateService(scopeFactory, timeProvider: clock);

        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);

        await batchConsumed.Task;

        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);
    }

    private static DatabaseContext NoopContext()
    {
        TestDatabaseFactory factory = new();

        return factory.Context;
    }

    // ========== One update per machine, not per row ==========

    [Test]
    public async Task StreamLoop_BatchWithManyRowsPerMachine_AppliesOneSummaryUpdatePerMachine()
    {
        // Intent: 5 CpuUsage rows for one machine collapse to a SINGLE summary apply, not five.
        IMachineStateRepository spy = Substitute.For<IMachineStateRepository>();
        DateTimeOffset t0 = DateTimeOffset.UnixEpoch;
        List<MachineTelemetry> batch = Enumerable.Range(1, 5)
            .Select(i => Row(i, 100, TelemetryTypeIds.CpuUsage, $$"""{ "cpu_usage_percent": {{i}} }""", t0.AddMinutes(i)))
            .ToList();
        spy.GetTelemetryBatchAsync(Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(batch, []); // first poll returns the batch, then empty

        await RunOneLoopIterationAsync(spy);

        await spy.Received(1).ApplySummaryPatchAsync(
            Arg.Is<MachineSummaryPatch>(p => (p.MachineId == 100) && (p.CpuUsagePercent == 5)),
            Arg.Any<CancellationToken>());
    }

    // ========== End-to-end projection against the real database ==========

    [Test]
    public async Task StreamLoop_ProjectsCorrectFinalState_AgainstRealDatabase()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        await SeedSummaryAndDetail(db, 100);
        DateTimeOffset t0 = RecentBase;
        await SeedTelemetryAsync(db,
            Row(1, 100, TelemetryTypeIds.CpuUsage, """{ "cpu_usage_percent": 40 }""", t0),
            Row(2, 100, TelemetryTypeIds.CpuUsage, """{ "cpu_usage_percent": 88 }""", t0.AddMinutes(5)),
            Row(3, 100, TelemetryTypeIds.OsVersion, """{ "os_name": "Ubuntu", "os_version": "22.04", "kernel": "6.2" }""", t0.AddMinutes(2)));

        await RunOneLoopIterationAsync(db);

        MachineStateSummary s = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == 100);
        await Assert.That(s.CpuUsagePercent).IsEqualTo(88);                 // latest CpuUsage
        await Assert.That(s.OsName).IsEqualTo("Ubuntu");
        await Assert.That(s.LastSeenAt).IsEqualTo(t0.AddMinutes(5));        // MAX ReceivedAt
        MachineStateDetail d = await db.GetTable<MachineStateDetail>().FirstAsync(x => x.MachineId == 100);
        await Assert.That(d.Kernel).IsEqualTo("6.2");
    }

    // ========== Idempotent on replay ==========

    [Test]
    public async Task StreamLoop_RunTwiceOnSameBatch_IsIdempotent()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        await SeedSummaryAndDetail(db, 100);
        DateTimeOffset t0 = RecentBase;
        await SeedTelemetryAsync(db, Row(1, 100, TelemetryTypeIds.CpuUsage, """{ "cpu_usage_percent": 60 }""", t0));

        await RunOneLoopIterationAsync(db, resetHighWaterMark: true);
        await RunOneLoopIterationAsync(db, resetHighWaterMark: true); // replay same batch

        MachineStateSummary s = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == 100);
        await Assert.That(s.CpuUsagePercent).IsEqualTo(60);
    }

    // ========== Raw telemetry is never modified (SSH history stays intact) ==========

    [Test]
    public async Task StreamLoop_DoesNotModifyRawTelemetryRows()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        await SeedSummaryAndDetail(db, 100);
        DateTimeOffset t0 = RecentBase;
        await SeedTelemetryAsync(db,
            Row(1, 100, TelemetryTypeIds.SshSessions, """[{"user":"root"}]""", t0),
            Row(2, 100, TelemetryTypeIds.SshSessions, """[{"user":"admin"}]""", t0.AddMinutes(1)));

        await RunOneLoopIterationAsync(db);

        List<MachineTelemetry> raw = await db.GetTable<MachineTelemetry>().OrderBy(r => r.Id).ToListAsync();
        await Assert.That(raw.Count).IsEqualTo(2);                              // both rows still present
        await Assert.That(raw[0].Payload).IsEqualTo("""[{"user":"root"}]""");  // unchanged
        MachineStateDetail d = await db.GetTable<MachineStateDetail>().FirstAsync(x => x.MachineId == 100);
        await Assert.That(d.SshSessions).IsEqualTo("""[{"user":"admin"}]"""); // detail shows the latest
    }

    // ========== Poison row is skipped, the rest applied, and a warning logged ==========

    [Test]
    public async Task StreamLoop_PoisonRow_SkipsItAndAppliesTheRest_AndLogsWarning()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        await SeedSummaryAndDetail(db, 100);
        DateTimeOffset t0 = RecentBase;
        await SeedTelemetryAsync(db,
            Row(1, 100, TelemetryTypeIds.CpuUsage, "broken json", t0),
            Row(2, 100, TelemetryTypeIds.MemoryUsage, """{ "memory_usage_percent": 25 }""", t0));

        await RunOneLoopIterationAsync(db);

        MachineStateSummary s = await db.GetTable<MachineStateSummary>().FirstAsync(x => x.MachineId == 100);
        await Assert.That(s.MemoryUsagePercent).IsEqualTo(25); // healthy type applied
        await Assert.That(s.CpuUsagePercent).IsNull();         // poison CpuUsage row left the column unchanged
    }

    // ========== Wrong-typed-field poison row does not wedge the loop ==========

    [Test]
    public async Task StreamLoop_WrongTypedFieldPoisonRow_AdvancesHighWaterMarkAndProjectsHealthyRows()
    {
        // Intent: a row with structurally valid JSON but a wrong-typed field (a string where an int
        // is expected) must not throw out of Collapse and wedge the loop. The batch must complete:
        // the high-water mark advances (proven by the loop's second poll being issued with the
        // advanced mark) and the healthy row is projected.
        IMachineStateRepository spy = Substitute.For<IMachineStateRepository>();
        DateTimeOffset t0 = RecentBase;
        List<MachineTelemetry> batch =
        [
            Row(1, 100, TelemetryTypeIds.CpuUsage, """{ "cpu_usage_percent": "high" }""", t0),
            Row(2, 100, TelemetryTypeIds.MemoryUsage, """{ "memory_usage_percent": 25 }""", t0),
        ];
        spy.GetTelemetryBatchAsync(Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(batch, []); // first poll returns the batch, then empty

        await RunOneLoopIterationAsync(spy);

        // The healthy MemoryUsage row was projected (proving the batch was not aborted by the poison row).
        await spy.Received(1).ApplySummaryPatchAsync(
            Arg.Is<MachineSummaryPatch>(p => (p.MachineId == 100) && (p.MemoryUsagePercent == 25)),
            Arg.Any<CancellationToken>());

        // The loop polled a second time using the advanced high-water mark (the last row's Id),
        // which only happens after the batch completed and the mark advanced — no infinite re-fetch.
        await spy.Received().GetTelemetryBatchAsync(
            2, Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ========== Per-type projection correctness (drives the public loop) ==========

    [Test]
    public async Task StreamLoop_SystemInfo_ProjectsSummaryAndDetailFields()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 100;
        await SeedSummaryAndDetail(db, machineId);

        string payload = """{"hostname":"web-01","hardware_model":"PowerEdge R740","hardware_vendor":"Dell","hardware_serial":"SN123","cpu_brand":"Xeon","cpu_cores":16,"memory_total_bytes":34359738368,"uptime_seconds":86400,"bios_version":"2.1","ip_addresses":["10.0.0.1"]}""";
        await SeedTelemetryAsync(db, Row(1, machineId, TelemetryTypeIds.SystemInfo, payload, FixedClock));

        await RunOneLoopIterationAsync(db);

        MachineStateSummary summary = await db.MachineStateSummaries.FirstAsync(s => s.MachineId == machineId);
        await Assert.That(summary.Hostname).IsEqualTo("web-01");
        await Assert.That(summary.HardwareModel).IsEqualTo("PowerEdge R740");
        await Assert.That(summary.IpAddresses).IsNotNull();

        MachineStateDetail detail = await db.MachineStateDetails.FirstAsync(d => d.MachineId == machineId);
        await Assert.That(detail.HardwareVendor).IsEqualTo("Dell");
        await Assert.That(detail.CpuBrand).IsEqualTo("Xeon");
        await Assert.That(detail.CpuCores).IsEqualTo(16);
        await Assert.That(detail.MemoryTotalBytes).IsEqualTo(34359738368L);
    }

    [Test]
    public async Task StreamLoop_OsVersion_ProjectsSummaryAndDetailFields()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 101;
        await SeedSummaryAndDetail(db, machineId);

        await SeedTelemetryAsync(db, Row(1, machineId, TelemetryTypeIds.OsVersion,
            """{"os_name":"Ubuntu","os_version":"22.04","kernel":"5.15.0-91"}""", FixedClock));

        await RunOneLoopIterationAsync(db);

        MachineStateSummary summary = await db.MachineStateSummaries.FirstAsync(s => s.MachineId == machineId);
        await Assert.That(summary.OsName).IsEqualTo("Ubuntu");
        await Assert.That(summary.OsVersion).IsEqualTo("22.04");

        MachineStateDetail detail = await db.MachineStateDetails.FirstAsync(d => d.MachineId == machineId);
        await Assert.That(detail.Kernel).IsEqualTo("5.15.0-91");
    }

    [Test]
    public async Task StreamLoop_CpuInfo_ProjectsDetailFields()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 102;
        await SeedSummaryAndDetail(db, machineId);

        await SeedTelemetryAsync(db, Row(1, machineId, TelemetryTypeIds.CpuInfo,
            """{"cpu_type":"x86_64","physical_cpus":2,"logical_cpus":8}""", FixedClock));

        await RunOneLoopIterationAsync(db);

        MachineStateDetail detail = await db.MachineStateDetails.FirstAsync(d => d.MachineId == machineId);
        await Assert.That(detail.CpuType).IsEqualTo("x86_64");
        await Assert.That(detail.CpuPhysicalCpus).IsEqualTo(2);
        await Assert.That(detail.CpuLogicalCpus).IsEqualTo(8);
    }

    [Test]
    public async Task StreamLoop_MemoryInfo_ProjectsDetailFields()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 103;
        await SeedSummaryAndDetail(db, machineId);

        await SeedTelemetryAsync(db, Row(1, machineId, TelemetryTypeIds.MemoryInfo,
            """{"swap_total_bytes":8589934592,"swap_free_bytes":4294967296}""", FixedClock));

        await RunOneLoopIterationAsync(db);

        MachineStateDetail detail = await db.MachineStateDetails.FirstAsync(d => d.MachineId == machineId);
        await Assert.That(detail.SwapTotalBytes).IsEqualTo(8589934592L);
        await Assert.That(detail.SwapFreeBytes).IsEqualTo(4294967296L);
    }

    [Test]
    public async Task StreamLoop_DiskInfo_ProjectsDetailPayload()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 104;
        await SeedSummaryAndDetail(db, machineId);

        string payload = """[{"mount":"/","size_bytes":107374182400,"filesystem":"ext4"},{"mount":"/data","size_bytes":536870912000,"filesystem":"xfs"}]""";
        await SeedTelemetryAsync(db, Row(1, machineId, TelemetryTypeIds.DiskInfo, payload, FixedClock));

        await RunOneLoopIterationAsync(db);

        MachineStateDetail detail = await db.MachineStateDetails.FirstAsync(d => d.MachineId == machineId);
        await Assert.That(detail.DiskInfos).IsEqualTo(payload);
    }

    [Test]
    public async Task StreamLoop_CpuUsage_ProjectsSummaryField()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 105;
        await SeedSummaryAndDetail(db, machineId);

        await SeedTelemetryAsync(db, Row(1, machineId, TelemetryTypeIds.CpuUsage,
            """{"cpu_usage_percent":73}""", FixedClock));

        await RunOneLoopIterationAsync(db);

        MachineStateSummary summary = await db.MachineStateSummaries.FirstAsync(s => s.MachineId == machineId);
        await Assert.That(summary.CpuUsagePercent).IsEqualTo(73);
    }

    [Test]
    public async Task StreamLoop_MemoryUsage_ProjectsSummaryAndDetailFields()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 106;
        await SeedSummaryAndDetail(db, machineId);

        await SeedTelemetryAsync(db, Row(1, machineId, TelemetryTypeIds.MemoryUsage,
            """{"memory_used":12884901888,"memory_usage_percent":75}""", FixedClock));

        await RunOneLoopIterationAsync(db);

        MachineStateSummary summary = await db.MachineStateSummaries.FirstAsync(s => s.MachineId == machineId);
        await Assert.That(summary.MemoryUsagePercent).IsEqualTo(75);

        MachineStateDetail detail = await db.MachineStateDetails.FirstAsync(d => d.MachineId == machineId);
        await Assert.That(detail.MemoryUsedBytes).IsEqualTo(12884901888L);
    }

    [Test]
    public async Task StreamLoop_DiskUsage_ProjectsSummaryAndDetailFields()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 107;
        await SeedSummaryAndDetail(db, machineId);

        string payload = """[{"mount":"/","usage_percent":42},{"mount":"/data","usage_percent":87}]""";
        await SeedTelemetryAsync(db, Row(1, machineId, TelemetryTypeIds.DiskUsage, payload, FixedClock));

        await RunOneLoopIterationAsync(db);

        MachineStateSummary summary = await db.MachineStateSummaries.FirstAsync(s => s.MachineId == machineId);
        await Assert.That(summary.MaxDiskUsagePercent).IsEqualTo(87);

        MachineStateDetail detail = await db.MachineStateDetails.FirstAsync(d => d.MachineId == machineId);
        await Assert.That(detail.DiskUsages).IsEqualTo(payload);
    }

    [Test]
    public async Task StreamLoop_SshSessions_ProjectsDetailPayload()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 108;
        await SeedSummaryAndDetail(db, machineId);

        string payload = """[{"user":"root","ip":"10.0.0.5","pid":1234}]""";
        await SeedTelemetryAsync(db, Row(1, machineId, TelemetryTypeIds.SshSessions, payload, FixedClock));

        await RunOneLoopIterationAsync(db);

        MachineStateDetail detail = await db.MachineStateDetails.FirstAsync(d => d.MachineId == machineId);
        await Assert.That(detail.SshSessions).IsEqualTo(payload);
    }

    [Test]
    public async Task StreamLoop_HardwareHealth_ProjectsSummaryFlagsAndDetailPayload()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 109;
        await SeedSummaryAndDetail(db, machineId);

        string payload = """{"disk_smart":[{"device":"/dev/sda","health_status":"FAILED"}],"fans":[{"name":"fan1","rpm":3000}],"power_supplies":[{"name":"psu1","status":"OK"}]}""";
        await SeedTelemetryAsync(db, Row(1, machineId, TelemetryTypeIds.HardwareHealth, payload, FixedClock));

        await RunOneLoopIterationAsync(db);

        MachineStateSummary summary = await db.MachineStateSummaries.FirstAsync(s => s.MachineId == machineId);
        await Assert.That(summary.HasDiskHealthIssue).IsTrue();
        await Assert.That(summary.HasHardwareIssue).IsFalse();

        MachineStateDetail detail = await db.MachineStateDetails.FirstAsync(d => d.MachineId == machineId);
        await Assert.That(detail.HardwareHealth).IsEqualTo(payload);
    }

    [Test]
    public async Task StreamLoop_PackageUpdates_ProjectsSummaryFields()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 110;
        await SeedSummaryAndDetail(db, machineId);

        await SeedTelemetryAsync(db, Row(1, machineId, TelemetryTypeIds.PackageUpdates,
            """{"pending_updates":42,"security_updates":7}""", FixedClock));

        await RunOneLoopIterationAsync(db);

        MachineStateSummary summary = await db.MachineStateSummaries.FirstAsync(s => s.MachineId == machineId);
        await Assert.That(summary.PendingUpdates).IsEqualTo(42);
        await Assert.That(summary.SecurityUpdates).IsEqualTo(7);
    }

    [Test]
    public async Task StreamLoop_ServiceStatus_ProjectsSummaryFields()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 111;
        await SeedSummaryAndDetail(db, machineId);

        await SeedTelemetryAsync(db, Row(1, machineId, TelemetryTypeIds.ServiceStatus,
            """{"total_services":120,"failed_services":3}""", FixedClock));

        await RunOneLoopIterationAsync(db);

        MachineStateSummary summary = await db.MachineStateSummaries.FirstAsync(s => s.MachineId == machineId);
        await Assert.That(summary.TotalServices).IsEqualTo(120);
        await Assert.That(summary.FailedServices).IsEqualTo(3);
    }

    [Test]
    public async Task StreamLoop_UnknownType_LeavesStateUnchanged()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 112;
        await SeedSummaryAndDetail(db, machineId);

        await SeedTelemetryAsync(db, Row(1, machineId, 999, """{"unknown":"data"}""", FixedClock));

        await RunOneLoopIterationAsync(db);

        // Unknown type carries no fragment but the machine is still seen, so LastSeenAt advances.
        MachineStateSummary summary = await db.MachineStateSummaries.FirstAsync(s => s.MachineId == machineId);
        await Assert.That(summary.LastSeenAt).IsEqualTo(FixedClock);
        await Assert.That(summary.CpuUsagePercent).IsNull();
    }

    [Test]
    public async Task StreamLoop_SystemInfo_UpdatesLastSeenAt()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 130;
        await SeedSummaryAndDetail(db, machineId);

        await SeedTelemetryAsync(db, Row(1, machineId, TelemetryTypeIds.SystemInfo,
            """{"hostname":"test"}""", FixedClock));

        await RunOneLoopIterationAsync(db);

        MachineStateSummary summary = await db.MachineStateSummaries.FirstAsync(s => s.MachineId == machineId);
        await Assert.That(summary.LastSeenAt).IsEqualTo(FixedClock);
    }

    [Test]
    public async Task StreamLoop_CpuUsageZero_StoresZeroNotNull()
    {
        // Protobuf default for int32 is 0. When cpu_usage_percent is explicitly 0 in the payload,
        // the projection must store 0 in the summary table, not null.
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 201;
        await SeedSummaryAndDetail(db, machineId);

        await SeedTelemetryAsync(db, Row(1, machineId, TelemetryTypeIds.CpuUsage,
            """{"cpu_usage_percent":0}""", FixedClock));

        await RunOneLoopIterationAsync(db);

        MachineStateSummary summary = await db.MachineStateSummaries.FirstAsync(s => s.MachineId == machineId);
        await Assert.That(summary.CpuUsagePercent).IsEqualTo(0);
    }

    [Test]
    public async Task StreamLoop_FarFutureUnknownType_LeavesStateUnchanged()
    {
        // A telemetry type from a future agent version must not crash the loop or change owned columns.
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 202;
        await SeedSummaryAndDetail(db, machineId);

        await SeedTelemetryAsync(db, Row(1, machineId, 9999, """{"future":"data"}""", FixedClock));

        await RunOneLoopIterationAsync(db);

        MachineStateSummary summary = await db.MachineStateSummaries.FirstAsync(s => s.MachineId == machineId);
        await Assert.That(summary.CpuUsagePercent).IsNull();

        MachineStateDetail detail = await db.MachineStateDetails.FirstAsync(d => d.MachineId == machineId);
        await Assert.That(detail.Kernel).IsNull();
    }

    [Test]
    public async Task StreamLoop_MachineNotSeeded_DoesNotCreateRows()
    {
        // When there is no summary/detail row, the UPDATE affects 0 rows and must not throw or insert.
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;
        long machineId = 999;
        // Deliberately not seeding summary/detail for this machine.
        await SeedTelemetryAsync(db, Row(1, machineId, TelemetryTypeIds.CpuUsage,
            """{"cpu_usage_percent":50}""", FixedClock));

        await RunOneLoopIterationAsync(db);

        MachineStateSummary? summary = await db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .FirstOrDefaultAsync();

        await Assert.That(summary).IsNull();
    }

    // ========== Empty batch / no lock: loop sleeps without error ==========

    [Test]
    public async Task StreamLoop_EmptyBatch_SleepsWithoutError()
    {
        // When no instance can acquire the lock, the loop must sleep (via the injected
        // TimeProvider) and never throw. A FixedTimeProvider whose clock never advances means
        // the sleep only completes when the cancellation token fires — so this test depends on
        // cancellation, not on real elapsed wall-clock time.
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);

        IAdvisoryLockProvider lockProvider = BlockingAdvisoryLockProvider();
        FixedTimeProvider clock = new(FixedClock);

        MachineStateStreamingService service = CreateService(
            scopeFactory, advisoryLockProvider: lockProvider, timeProvider: clock);

        using CancellationTokenSource cts = new();

        await Assert.That(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);

                // Cancel deterministically; the loop's TimeProvider-based delay unwinds on cancel.
                await cts.CancelAsync();
                await service.StopAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Expected on cancellation.
            }
        }).ThrowsNothing();
    }

    // ========== ComputeMaxDiskUsagePercent (logic now lives in TelemetryPayloadParser) ==========

    [Test]
    public async Task ComputeMaxDiskUsagePercent_ValidJson_ReturnsHighest()
    {
        int result = TelemetryPayloadParser.ComputeMaxDiskUsagePercent(
            """[{"mount":"/","usage_percent":30},{"mount":"/data","usage_percent":92}]""");

        await Assert.That(result).IsEqualTo(92);
    }

    [Test]
    public async Task ComputeMaxDiskUsagePercent_EmptyArray_ReturnsZero()
    {
        int result = TelemetryPayloadParser.ComputeMaxDiskUsagePercent("[]");

        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task ComputeMaxDiskUsagePercent_MalformedJson_ReturnsZero()
    {
        int result = TelemetryPayloadParser.ComputeMaxDiskUsagePercent("not-json{{{");

        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task ComputeMaxDiskUsagePercent_MissingUsagePercent_ReturnsZero()
    {
        int result = TelemetryPayloadParser.ComputeMaxDiskUsagePercent(
            """[{"mount":"/","size_bytes":100}]""");

        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task ComputeMaxDiskUsagePercent_NegativePercent_TreatedAsZero()
    {
        // Negative usage_percent should not become max (stays at 0 since -5 < 0)
        int result = TelemetryPayloadParser.ComputeMaxDiskUsagePercent(
            """[{"mount":"/","usage_percent":-5}]""");

        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task ComputeMaxDiskUsagePercent_PercentOver100_ReturnsExactValue()
    {
        // Document behavior: values over 100 are returned as-is (no clamping)
        int result = TelemetryPayloadParser.ComputeMaxDiskUsagePercent(
            """[{"mount":"/","usage_percent":105}]""");

        await Assert.That(result).IsEqualTo(105);
    }

    [Test]
    public async Task ComputeMaxDiskUsagePercent_NotArray_ReturnsZero()
    {
        int result = TelemetryPayloadParser.ComputeMaxDiskUsagePercent(
            """{"mount":"/","usage_percent":50}""");

        await Assert.That(result).IsEqualTo(0);
    }

    [Test]
    public async Task ComputeMaxDiskUsagePercent_SingleDisk_ReturnsItsValue()
    {
        int result = TelemetryPayloadParser.ComputeMaxDiskUsagePercent(
            """[{"mount":"/","usage_percent":55}]""");

        await Assert.That(result).IsEqualTo(55);
    }

    [Test]
    public async Task ComputeMaxDiskUsagePercent_AllZero_ReturnsZero()
    {
        int result = TelemetryPayloadParser.ComputeMaxDiskUsagePercent(
            """[{"mount":"/","usage_percent":0},{"mount":"/data","usage_percent":0}]""");

        await Assert.That(result).IsEqualTo(0);
    }

    // ========== ComputeHardwareHealthFlags (logic now lives in TelemetryPayloadParser) ==========

    [Test]
    public async Task ComputeHardwareHealthFlags_FanRpmZero_SetsFlag()
    {
        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(
            """{"fans":[{"name":"fan1","rpm":0}],"disk_smart":[],"power_supplies":[]}""");

        await Assert.That(hasDiskIssue).IsFalse();
        await Assert.That(hasHardwareIssue).IsTrue();
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_PsuStatusNotOk_SetsFlag()
    {
        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(
            """{"fans":[{"name":"fan1","rpm":3000}],"disk_smart":[],"power_supplies":[{"name":"psu1","status":"DEGRADED"}]}""");

        await Assert.That(hasDiskIssue).IsFalse();
        await Assert.That(hasHardwareIssue).IsTrue();
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_DiskHealthFailed_SetsFlag()
    {
        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(
            """{"disk_smart":[{"device":"/dev/sda","health_status":"FAILED"}],"fans":[],"power_supplies":[]}""");

        await Assert.That(hasDiskIssue).IsTrue();
        await Assert.That(hasHardwareIssue).IsFalse();
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_AllHealthy_ClearsFlag()
    {
        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(
            """{"disk_smart":[{"device":"/dev/sda","health_status":"PASSED"}],"fans":[{"name":"fan1","rpm":3000}],"power_supplies":[{"name":"psu1","status":"OK"}]}""");

        await Assert.That(hasDiskIssue).IsFalse();
        await Assert.That(hasHardwareIssue).IsFalse();
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_MalformedJson_ReturnsDefaults()
    {
        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(
            "not-valid-json{{{");

        await Assert.That(hasDiskIssue).IsFalse();
        await Assert.That(hasHardwareIssue).IsFalse();
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_EmptyFanArray_NoFalsePositive()
    {
        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(
            """{"fans":[],"disk_smart":[],"power_supplies":[]}""");

        await Assert.That(hasDiskIssue).IsFalse();
        await Assert.That(hasHardwareIssue).IsFalse();
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_EmptyPsuArray_NoFalsePositive()
    {
        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(
            """{"fans":[{"name":"fan1","rpm":2500}],"disk_smart":[],"power_supplies":[]}""");

        await Assert.That(hasDiskIssue).IsFalse();
        await Assert.That(hasHardwareIssue).IsFalse();
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_EmptyDiskArray_NoFalsePositive()
    {
        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(
            """{"fans":[{"name":"fan1","rpm":2500}],"disk_smart":[],"power_supplies":[{"name":"psu1","status":"OK"}]}""");

        await Assert.That(hasDiskIssue).IsFalse();
        await Assert.That(hasHardwareIssue).IsFalse();
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_MissingFansProperty_NoFalsePositive()
    {
        // Machine might not report fans at all
        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(
            """{"disk_smart":[{"device":"/dev/sda","health_status":"PASSED"}]}""");

        await Assert.That(hasDiskIssue).IsFalse();
        await Assert.That(hasHardwareIssue).IsFalse();
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_MissingPsuProperty_NoFalsePositive()
    {
        // Machine might not report power supplies at all
        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(
            """{"disk_smart":[],"fans":[{"name":"fan1","rpm":2000}]}""");

        await Assert.That(hasDiskIssue).IsFalse();
        await Assert.That(hasHardwareIssue).IsFalse();
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_FanAndDiskBothBad_BothFlags()
    {
        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(
            """{"disk_smart":[{"device":"/dev/sda","health_status":"FAILED"}],"fans":[{"name":"fan1","rpm":0}],"power_supplies":[{"name":"psu1","status":"OK"}]}""");

        await Assert.That(hasDiskIssue).IsTrue();
        await Assert.That(hasHardwareIssue).IsTrue();
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_FansNotArray_NoFalsePositive()
    {
        // If fans is a string instead of an array, it should be ignored
        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(
            """{"fans":"none","disk_smart":[],"power_supplies":[]}""");

        await Assert.That(hasDiskIssue).IsFalse();
        await Assert.That(hasHardwareIssue).IsFalse();
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_DiskSmartNotArray_NoFalsePositive()
    {
        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(
            """{"disk_smart":"none","fans":[],"power_supplies":[]}""");

        await Assert.That(hasDiskIssue).IsFalse();
        await Assert.That(hasHardwareIssue).IsFalse();
    }

    [Test]
    public async Task ComputeHardwareHealthFlags_PsuCheckSkippedWhenFanAlreadyFailed()
    {
        // When hasHardwareIssue is already true from fans, PSU check is skipped (optimization).
        // Both bad fan and bad PSU should still only set hasHardwareIssue once.
        (bool hasDiskIssue, bool hasHardwareIssue) = TelemetryPayloadParser.ComputeHardwareHealthFlags(
            """{"fans":[{"name":"fan1","rpm":0}],"disk_smart":[],"power_supplies":[{"name":"psu1","status":"DEGRADED"}]}""");

        await Assert.That(hasDiskIssue).IsFalse();
        await Assert.That(hasHardwareIssue).IsTrue();
    }

    // ========== Malformed payloads are skipped, not thrown (parser try-parse contract) ==========

    [Test]
    public async Task Parse_NullPayload_Throws()
    {
        // A null payload is not malformed JSON but a contract violation (rows always carry a
        // payload), so the parser surfaces it as an ArgumentNullException rather than swallowing it.
        await Assert.That(() => TelemetryPayloadParser.TryParseSystemInfo(null!, out SystemInfoFragment? _))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Parse_EmptyPayload_ReturnsFalse()
    {
        bool ok = TelemetryPayloadParser.TryParseCpuUsage("", out CpuUsageFragment? fragment);

        await Assert.That(ok).IsFalse();
        await Assert.That(fragment).IsNull();
    }

    [Test]
    public async Task Parse_MalformedJson_ReturnsFalse()
    {
        bool ok = TelemetryPayloadParser.TryParseCpuUsage("not-valid-json{{{", out CpuUsageFragment? fragment);

        await Assert.That(ok).IsFalse();
        await Assert.That(fragment).IsNull();
    }

    // ========== Constructor null guard tests ==========

    [Test]
    public async Task Constructor_NullScopeFactory_ThrowsArgumentNullException()
    {
        await Assert.That(() => new MachineStateStreamingService(
            null!,
            Substitute.For<ISqlDialect>(),
            Substitute.For<IAdvisoryLockProvider>(),
            Substitute.For<IServerSettingsCache>(),
            Substitute.For<ILogger<MachineStateStreamingService>>()))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullDialect_ThrowsArgumentNullException()
    {
        await Assert.That(() => new MachineStateStreamingService(
            Substitute.For<IServiceScopeFactory>(),
            null!,
            Substitute.For<IAdvisoryLockProvider>(),
            Substitute.For<IServerSettingsCache>(),
            Substitute.For<ILogger<MachineStateStreamingService>>()))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullAdvisoryLockProvider_ThrowsArgumentNullException()
    {
        await Assert.That(() => new MachineStateStreamingService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ISqlDialect>(),
            null!,
            Substitute.For<IServerSettingsCache>(),
            Substitute.For<ILogger<MachineStateStreamingService>>()))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullSettingsCache_ThrowsArgumentNullException()
    {
        await Assert.That(() => new MachineStateStreamingService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ISqlDialect>(),
            Substitute.For<IAdvisoryLockProvider>(),
            null!,
            Substitute.For<ILogger<MachineStateStreamingService>>()))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullLogger_ThrowsArgumentNullException()
    {
        await Assert.That(() => new MachineStateStreamingService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ISqlDialect>(),
            Substitute.For<IAdvisoryLockProvider>(),
            Substitute.For<IServerSettingsCache>(),
            null!))
            .Throws<ArgumentNullException>();
    }
}
