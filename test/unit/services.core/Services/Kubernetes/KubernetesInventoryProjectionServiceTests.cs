// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Grpc.AgentTelemetry;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Kubernetes;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading.Channels;
using TUnit.Assertions.Enums;

namespace Framlux.FleetManagement.Test.Services.Kubernetes;

/// <summary>
/// Tests for <see cref="KubernetesInventoryProjectionService"/>. The service runs against a real
/// SQLite database through a substitute repository that forwards to the real one, so a test can make
/// a single call fail while everything else behaves as in production. Time is a
/// <see cref="FakeTimeProvider"/>: every delay the service starts is observed and released by the
/// test, so no test waits on the wall clock and none depends on how quickly the service runs.
/// </summary>
public class KubernetesInventoryProjectionServiceTests
{
    private const string FingerprintA = "a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718";

    private static readonly DateTimeOffset Start = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromSeconds(5);

    private readonly record struct ParkedDelay(DateTimeOffset FiresAt, TimeSpan Delay);

    /// <summary>
    /// A fake clock that reports every timer the service creates. A delay is the only place the
    /// service waits, so a test that has read the delay knows the service is parked and is free to
    /// inspect the database and the substitutes before releasing it.
    /// </summary>
    private sealed class ParkingTimeProvider : FakeTimeProvider
    {
        private readonly Channel<ParkedDelay> _delays = Channel.CreateUnbounded<ParkedDelay>();

        public ParkingTimeProvider(DateTimeOffset start)
            : base(start)
        {
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = base.CreateTimer(callback, state, dueTime, period);
            _delays.Writer.TryWrite(new ParkedDelay(GetUtcNow() + dueTime, dueTime));

            return timer;
        }

        /// <summary>Waits for the next delay the service starts, without releasing it.</summary>
        public async Task<ParkedDelay> NextDelayAsync()
        {
            return await _delays.Reader.ReadAsync();
        }

        /// <summary>Waits for the next delay, checks its length and advances the clock past it.</summary>
        public async Task ReleaseNextAsync(TimeSpan expected)
        {
            ParkedDelay parked = await NextDelayAsync();
            await Assert.That(parked.Delay).IsEqualTo(expected);

            Advance(parked.Delay);
        }

        /// <summary>
        /// Waits until the service has started a delay that ends at or after the given instant, which
        /// is when it has worked through every poll before it.
        /// </summary>
        public async Task WaitUntilParkedPastAsync(DateTimeOffset firesAt)
        {
            ParkedDelay parked;
            do
            {
                parked = await NextDelayAsync();
            }
            while (parked.FiresAt < firesAt);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentQueue<long> _applyAttempts = new();
        private readonly ConcurrentQueue<(long TelemetryId, LocalReportOutcome Outcome)> _outcomes = new();
        private int _cursorReads;
        private int _garbageCollections;

        public Rig(IAdvisoryLockProvider? lockProvider = null, int visibilityLagSeconds = 5)
        {
            IMeterFactory meterFactory = TestMetricsFactory.CreateMeterFactory();
            Failures = new MetricCollector<long>(meterFactory, VordMeter.Name, "vord.kubernetes.projection_failures");

            Real = new DatabaseRepository(Db.Context, NullLogger<DatabaseRepository>.Instance);
            Repository = Substitute.For<IKubernetesInventoryRepository>();
            ForwardEverythingToTheRealRepository();

            TestServiceScopeFactory scopeFactory = new(
                Db.Context,
                new Dictionary<Type, object> { [typeof(IKubernetesInventoryRepository)] = Repository });

            Service = new KubernetesInventoryProjectionService(
                scopeFactory,
                lockProvider ?? AcquiringLockProvider(),
                Logger,
                Microsoft.Extensions.Options.Options.Create(new StreamingOptions { VisibilityLagSeconds = visibilityLagSeconds }),
                new KubernetesInventoryMetrics(meterFactory),
                Clock,
                TimeSpan.Zero);
        }

        public TestDatabaseFactory Db { get; } = new();

        public ParkingTimeProvider Clock { get; } = new(Start);

        public IKubernetesInventoryRepository Real { get; }

        public IKubernetesInventoryRepository Repository { get; }

        public FakeLogger<KubernetesInventoryProjectionService> Logger { get; } = new();

        public MetricCollector<long> Failures { get; }

        public KubernetesInventoryProjectionService Service { get; }

        /// <summary>
        /// Decides, per apply call, whether it fails: given the report and how many times that
        /// telemetry row has been attempted so far (this call included), it returns the exception to
        /// fail with, or null to let the call through to the real repository.
        /// </summary>
        public Func<LocalNodeReport, int, Exception?> ApplyFault { get; set; } = (_, _) => null;

        public long[] ApplyAttempts => [.. _applyAttempts];

        public (long TelemetryId, LocalReportOutcome Outcome)[] Outcomes => [.. _outcomes];

        public int CursorReads => Volatile.Read(ref _cursorReads);

        public int GarbageCollections => Volatile.Read(ref _garbageCollections);

        public async Task StartAsync()
        {
            await Service.StartAsync(_cts.Token);
        }

        public async Task<long?> CursorAsync()
        {
            return await Real.GetProjectionCursorAsync(CancellationToken.None);
        }

        public long FailureCount(string reason)
        {
            return Failures.GetMeasurementSnapshot()
                .Where(m => (string?)m.Tags["reason"] == reason)
                .Sum(m => m.Value);
        }

        public async Task<int> SeedTenantAsync()
        {
            return await Db.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant());
        }

        public async Task<long> SeedMachineAsync(int tenantId, bool isDeleted = false)
        {
            Machine machine = TestDataBuilder.BuildMachine(tenantId);
            machine.IsDeleted = isDeleted;

            return await Db.Context.InsertWithInt64IdentityAsync(machine);
        }

        public async Task<long> SeedRowAsync(
            long machineId, int tenantId, string payload, short telemetryType = 14, DateTimeOffset? serverReceivedAt = null)
        {
            DateTimeOffset receivedAt = Start.AddMinutes(-1);

            return await Db.Context.InsertWithInt64IdentityAsync(new MachineTelemetry
            {
                MachineId = machineId,
                TenantId = tenantId,
                TelemetryType = telemetryType,
                Payload = payload,
                ReceivedAt = receivedAt,
                ServerReceivedAt = serverReceivedAt ?? receivedAt,
                SourceEventId = Guid.NewGuid().ToString("N"),
            });
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            await Service.StopAsync(CancellationToken.None);

            Failures.Dispose();
            Db.Dispose();
            _cts.Dispose();
        }

        private void ForwardEverythingToTheRealRepository()
        {
            Repository.GetProjectionCursorAsync(Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    Interlocked.Increment(ref _cursorReads);

                    return Real.GetProjectionCursorAsync(ci.Arg<CancellationToken>());
                });
            Repository.SetProjectionCursorAsync(Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(ci => Real.SetProjectionCursorAsync(ci.ArgAt<long>(0), ci.ArgAt<DateTimeOffset>(1), ci.ArgAt<CancellationToken>(2)));
            Repository.GetKubernetesTelemetryBatchAsync(
                    Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(ci => Real.GetKubernetesTelemetryBatchAsync(
                    ci.ArgAt<long>(0), ci.ArgAt<DateTimeOffset>(1), ci.ArgAt<DateTimeOffset>(2), ci.ArgAt<int>(3), ci.ArgAt<CancellationToken>(4)));
            Repository.ApplyLocalNodeReportAsync(Arg.Any<LocalNodeReport>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(async ci =>
                {
                    LocalNodeReport report = ci.ArgAt<LocalNodeReport>(0);
                    _applyAttempts.Enqueue(report.TelemetryId);

                    Exception? fault = ApplyFault(report, _applyAttempts.Count(id => id == report.TelemetryId));
                    if (fault is not null)
                    {
                        throw fault;
                    }

                    LocalReportOutcome outcome = await Real.ApplyLocalNodeReportAsync(
                        report, ci.ArgAt<DateTimeOffset>(1), ci.ArgAt<CancellationToken>(2));
                    _outcomes.Enqueue((report.TelemetryId, outcome));

                    return outcome;
                });
            Repository.CollectGarbageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(ci =>
                {
                    Interlocked.Increment(ref _garbageCollections);

                    return Real.CollectGarbageAsync(ci.ArgAt<DateTimeOffset>(0), ci.ArgAt<CancellationToken>(1));
                });
        }
    }

    private static IAdvisoryLockProvider AcquiringLockProvider()
    {
        IAdvisoryLockProvider provider = Substitute.For<IAdvisoryLockProvider>();
        IAdvisoryLock handle = Substitute.For<IAdvisoryLock>();
        handle.IsAliveAsync(Arg.Any<CancellationToken>()).Returns(true);
        provider.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(handle);

        return provider;
    }

    private static string NodePayload(
        string nodeName, bool hasKubelet = true, bool controlPlane = false, string fingerprint = FingerprintA)
    {
        return JsonSerializer.Serialize(
            new KubernetesNodeRecord
            {
                Distro = "k3s",
                KubeletVersion = "v1.30.4+k3s1",
                NodeName = nodeName,
                NodeNameSource = "certificate",
                HasKubelet = hasKubelet,
                ControlPlane = controlPlane,
                CaFingerprint = fingerprint,
            },
            JsonDefaults.SnakeCase);
    }

    [Test]
    public async Task Projector_AppliesType14RowsInOrderAndAdvancesCursor()
    {
        await using Rig rig = new();
        int tenantId = await rig.SeedTenantAsync();
        long first = await rig.SeedMachineAsync(tenantId);
        long second = await rig.SeedMachineAsync(tenantId);
        long rowA = await rig.SeedRowAsync(first, tenantId, NodePayload("node-a", controlPlane: true));
        long rowB = await rig.SeedRowAsync(second, tenantId, NodePayload("node-b"));
        long rowC = await rig.SeedRowAsync(first, tenantId, "{}", telemetryType: 15);

        await rig.StartAsync();
        await rig.Clock.NextDelayAsync();

        await Assert.That(rig.ApplyAttempts).IsEquivalentTo(new[] { rowA, rowB }, CollectionOrdering.Matching);
        await Assert.That(await rig.CursorAsync()).IsEqualTo(rowC);
        await Assert.That(await rig.Db.Context.KubernetesClusters.CountAsync()).IsEqualTo(1);
        await Assert.That(await rig.Db.Context.KubernetesNodes.CountAsync()).IsEqualTo(2);
        await Assert.That(rig.Failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Projector_InvalidPayload_SkipsCountsAndAdvances()
    {
        await using Rig rig = new();
        int tenantId = await rig.SeedTenantAsync();
        long first = await rig.SeedMachineAsync(tenantId);
        long second = await rig.SeedMachineAsync(tenantId);
        long invalidRow = await rig.SeedRowAsync(first, tenantId, "{}");
        long validRow = await rig.SeedRowAsync(second, tenantId, NodePayload("node-b"));

        await rig.StartAsync();
        await rig.Clock.NextDelayAsync();

        await Assert.That(rig.FailureCount("invalid_payload")).IsEqualTo(1L);
        await Assert.That(rig.Failures.GetMeasurementSnapshot().Count).IsEqualTo(1);
        await Assert.That(rig.ApplyAttempts).IsEquivalentTo(new[] { validRow }, CollectionOrdering.Matching);
        await Assert.That(await rig.CursorAsync()).IsEqualTo(validRow);
        await Assert.That(await rig.Db.Context.KubernetesNodes.CountAsync()).IsEqualTo(1);

        IReadOnlyList<FakeLogRecord> errors = [.. rig.Logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Error)];
        await Assert.That(errors.Count).IsEqualTo(1);
        await Assert.That(errors[0].Message).Contains(invalidRow.ToString());
    }

    [Test]
    public async Task Projector_KubeletNodeWithoutAName_IsSkippedAsInvalid()
    {
        await using Rig rig = new();
        int tenantId = await rig.SeedTenantAsync();
        long machineId = await rig.SeedMachineAsync(tenantId);
        long row = await rig.SeedRowAsync(machineId, tenantId, NodePayload(nodeName: "", hasKubelet: true));

        await rig.StartAsync();
        await rig.Clock.NextDelayAsync();

        await Assert.That(rig.FailureCount("invalid_payload")).IsEqualTo(1L);
        await Assert.That(rig.ApplyAttempts.Length).IsEqualTo(0);
        await Assert.That(await rig.CursorAsync()).IsEqualTo(row);
    }

    [Test]
    [Arguments("")]
    [Arguments("{}")]
    [Arguments("not json")]
    [Arguments("null")]
    [Arguments("[]")]
    [Arguments("""{"distro":"k3s","has_kubelet":"yes"}""")]
    [Arguments("""{"distro":"k3s","has_kubelet":true,"node_name":"Node_1"}""")]
    [Arguments("""{"distro":"k3s","has_kubelet":false,"node_name":"Node_1"}""")]
    [Arguments("""{"has_kubelet":true,"node_name":"node-1"}""")]
    public async Task Projector_UnusableStoredPayload_IsSkippedAsInvalid(string payload)
    {
        await using Rig rig = new();
        int tenantId = await rig.SeedTenantAsync();
        long machineId = await rig.SeedMachineAsync(tenantId);
        long row = await rig.SeedRowAsync(machineId, tenantId, payload);

        await rig.StartAsync();
        await rig.Clock.NextDelayAsync();

        await Assert.That(rig.FailureCount("invalid_payload")).IsEqualTo(1L);
        await Assert.That(rig.ApplyAttempts.Length).IsEqualTo(0);
        await Assert.That(await rig.CursorAsync()).IsEqualTo(row);
    }

    [Test]
    public async Task Projector_NoKubeletReportWithEmptyNodeName_IsAppliedAsNotANode()
    {
        // An agent on a control-plane host with no kubelet and an unusable hostname sends an empty
        // node name. That is a valid report of a host that is not a node, not a malformed payload.
        await using Rig rig = new();
        int tenantId = await rig.SeedTenantAsync();
        long machineId = await rig.SeedMachineAsync(tenantId);
        long row = await rig.SeedRowAsync(machineId, tenantId, NodePayload(nodeName: "", hasKubelet: false, controlPlane: true));

        await rig.StartAsync();
        await rig.Clock.NextDelayAsync();

        await Assert.That(rig.Failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
        await Assert.That(rig.Outcomes).IsEquivalentTo(new[] { (row, LocalReportOutcome.NotANode) }, CollectionOrdering.Matching);
        await Assert.That(await rig.CursorAsync()).IsEqualTo(row);
        await Assert.That(await rig.Db.Context.KubernetesNodes.CountAsync()).IsEqualTo(0);
        await Assert.That(await rig.Db.Context.KubernetesClusters.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task Projector_TransientFailure_RetriesWithoutAdvancing_ThenSucceeds()
    {
        await using Rig rig = new();
        int tenantId = await rig.SeedTenantAsync();
        long machineId = await rig.SeedMachineAsync(tenantId);
        long row = await rig.SeedRowAsync(machineId, tenantId, NodePayload("node-a"));
        rig.ApplyFault = (_, attempt) => attempt == 1 ? new TimeoutException("connection reset") : null;

        await rig.StartAsync();
        ParkedDelay afterFailure = await rig.Clock.NextDelayAsync();

        await Assert.That(afterFailure.Delay).IsEqualTo(PollInterval);
        await Assert.That(rig.ApplyAttempts.Length).IsEqualTo(1);
        await Assert.That(await rig.CursorAsync()).IsNull();
        await Assert.That(await rig.Db.Context.KubernetesNodes.CountAsync()).IsEqualTo(0);
        await Assert.That(rig.Failures.GetMeasurementSnapshot().Count).IsEqualTo(0);

        rig.Clock.Advance(afterFailure.Delay);
        await rig.Clock.NextDelayAsync();

        await Assert.That(rig.ApplyAttempts.Length).IsEqualTo(2);
        await Assert.That(await rig.CursorAsync()).IsEqualTo(row);
        await Assert.That(await rig.Db.Context.KubernetesNodes.CountAsync()).IsEqualTo(1);
        await Assert.That(rig.Failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Projector_TransientFailureFiveTimes_SkipsAndCounts()
    {
        await using Rig rig = new();
        int tenantId = await rig.SeedTenantAsync();
        long first = await rig.SeedMachineAsync(tenantId);
        long second = await rig.SeedMachineAsync(tenantId);
        long stuckRow = await rig.SeedRowAsync(first, tenantId, NodePayload("node-a"));
        long laterRow = await rig.SeedRowAsync(second, tenantId, NodePayload("node-b"));
        rig.ApplyFault = (report, _) => report.TelemetryId == stuckRow ? new TimeoutException("connection reset") : null;

        await rig.StartAsync();

        // Four failures each leave the row in place and wait; the fifth gives up on it. The database is
        // read only while the service is parked in a delay, since it shares one connection with the test.
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            ParkedDelay parked = await rig.Clock.NextDelayAsync();
            await Assert.That(parked.Delay).IsEqualTo(PollInterval);
            await Assert.That(await rig.CursorAsync()).IsNull();
            await Assert.That(rig.FailureCount("retries_exhausted")).IsEqualTo(0L);

            rig.Clock.Advance(parked.Delay);
        }

        await rig.Clock.NextDelayAsync();

        await Assert.That(rig.ApplyAttempts.Count(id => id == stuckRow)).IsEqualTo(5);
        await Assert.That(rig.FailureCount("retries_exhausted")).IsEqualTo(1L);
        await Assert.That(rig.Failures.GetMeasurementSnapshot().Count).IsEqualTo(1);
        await Assert.That(await rig.CursorAsync()).IsEqualTo(laterRow);
        await Assert.That(await rig.Db.Context.KubernetesNodes.CountAsync()).IsEqualTo(1);
        await Assert.That(rig.Clock.GetUtcNow() - Start).IsGreaterThanOrEqualTo(TimeSpan.FromSeconds(40));
    }

    [Test]
    public async Task Projector_NonTransientFailure_SkipsAndCounts()
    {
        await using Rig rig = new();
        int tenantId = await rig.SeedTenantAsync();
        long first = await rig.SeedMachineAsync(tenantId);
        long second = await rig.SeedMachineAsync(tenantId);
        long brokenRow = await rig.SeedRowAsync(first, tenantId, NodePayload("node-a"));
        long laterRow = await rig.SeedRowAsync(second, tenantId, NodePayload("node-b"));
        rig.ApplyFault = (report, _) => report.TelemetryId == brokenRow ? new InvalidOperationException("constraint violated") : null;

        await rig.StartAsync();
        await rig.Clock.NextDelayAsync();

        await Assert.That(rig.ApplyAttempts).IsEquivalentTo(new[] { brokenRow, laterRow }, CollectionOrdering.Matching);
        await Assert.That(rig.FailureCount("apply_failed")).IsEqualTo(1L);
        await Assert.That(rig.Failures.GetMeasurementSnapshot().Count).IsEqualTo(1);
        await Assert.That(await rig.CursorAsync()).IsEqualTo(laterRow);
        await Assert.That(await rig.Db.Context.KubernetesNodes.CountAsync()).IsEqualTo(1);
        await Assert.That(rig.Logger.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Error)).IsEqualTo(1);
    }

    [Test]
    public async Task Projector_RowNotYetVisible_IsNotApplied()
    {
        // The visibility lag is five seconds, so a row accepted one second ago may still be joined by
        // a lower-id row that commits late. It is read only once the clock has moved past the lag.
        await using Rig rig = new();
        int tenantId = await rig.SeedTenantAsync();
        long machineId = await rig.SeedMachineAsync(tenantId);
        long row = await rig.SeedRowAsync(machineId, tenantId, NodePayload("node-a"), serverReceivedAt: Start.AddSeconds(-1));

        await rig.StartAsync();
        ParkedDelay idle = await rig.Clock.NextDelayAsync();

        await Assert.That(rig.ApplyAttempts.Length).IsEqualTo(0);
        await Assert.That(await rig.CursorAsync()).IsNull();

        rig.Clock.Advance(idle.Delay);
        await rig.Clock.NextDelayAsync();

        await Assert.That(rig.ApplyAttempts).IsEquivalentTo(new[] { row }, CollectionOrdering.Matching);
        await Assert.That(await rig.CursorAsync()).IsEqualTo(row);
    }

    [Test]
    [Arguments(null, 0L)]
    [Arguments(41L, 41L)]
    public async Task Projector_ReadsFromTheCursorWithinTheWindowAndBehindTheLag(long? storedCursor, long expectedAfterId)
    {
        await using Rig rig = new();
        if (storedCursor is not null)
        {
            await rig.Real.SetProjectionCursorAsync(storedCursor.Value, Start, CancellationToken.None);
        }

        List<(long AfterId, DateTimeOffset ReceivedAfter, DateTimeOffset VisibleBefore, int BatchSize)> reads = [];
        rig.Repository.GetKubernetesTelemetryBatchAsync(
                Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                reads.Add((ci.ArgAt<long>(0), ci.ArgAt<DateTimeOffset>(1), ci.ArgAt<DateTimeOffset>(2), ci.ArgAt<int>(3)));

                return Task.FromResult<IReadOnlyList<MachineTelemetry>>([]);
            });

        await rig.StartAsync();
        await rig.Clock.NextDelayAsync();

        await Assert.That(reads.Count).IsEqualTo(1);
        await Assert.That(reads[0].AfterId).IsEqualTo(expectedAfterId);
        await Assert.That(reads[0].ReceivedAfter).IsEqualTo(Start.AddDays(-8));
        await Assert.That(reads[0].VisibleBefore).IsEqualTo(Start.AddSeconds(-5));
        await Assert.That(reads[0].BatchSize).IsEqualTo(200);
    }

    [Test]
    public async Task Projector_DeletedMachineRow_DoesNotLink()
    {
        await using Rig rig = new();
        int tenantId = await rig.SeedTenantAsync();
        long machineId = await rig.SeedMachineAsync(tenantId, isDeleted: true);
        long row = await rig.SeedRowAsync(machineId, tenantId, NodePayload("node-a"));

        await rig.StartAsync();
        await rig.Clock.NextDelayAsync();

        await Assert.That(rig.Outcomes).IsEquivalentTo(new[] { (row, LocalReportOutcome.MachineDeleted) }, CollectionOrdering.Matching);
        await Assert.That(await rig.CursorAsync()).IsEqualTo(row);
        await Assert.That(await rig.Db.Context.KubernetesNodes.CountAsync()).IsEqualTo(0);
        await Assert.That(await rig.Db.Context.KubernetesClusters.CountAsync()).IsEqualTo(0);
        await Assert.That(rig.Failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Projector_RepositoryAlwaysThrows_KeepsPollingAndDoesNotFault()
    {
        // A fault in the projector must never escape ExecuteAsync: an escaped exception stops the host,
        // and with it the state-streaming services that share the process.
        IAdvisoryLockProvider provider = AcquiringLockProvider();
        await using Rig rig = new(provider);
        InvalidOperationException outage = new("database unavailable");
        rig.Repository.GetProjectionCursorAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<long?>(outage));
        rig.Repository.GetKubernetesTelemetryBatchAsync(
                Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<MachineTelemetry>>(outage));
        rig.Repository.SetProjectionCursorAsync(Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(outage));
        rig.Repository.ApplyLocalNodeReportAsync(Arg.Any<LocalNodeReport>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<LocalReportOutcome>(outage));
        rig.Repository.CollectGarbageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(outage));

        await rig.StartAsync();
        for (int poll = 0; poll < 5; poll++)
        {
            await rig.Clock.ReleaseNextAsync(PollInterval);
        }

        await Assert.That(rig.Service.ExecuteTask!.IsCompleted).IsFalse();
        await Assert.That(rig.Logger.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Error)).IsGreaterThanOrEqualTo(5);

        // A failed poll keeps the lock rather than handing the stream to another replica each time.
        await provider.Received(1).TryAcquireAsync(LockNames.KubernetesInventoryProjection, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Projector_LockProviderAlwaysThrows_KeepsRetryingAndDoesNotFault()
    {
        IAdvisoryLockProvider provider = Substitute.For<IAdvisoryLockProvider>();
        provider.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IAdvisoryLock?>(new InvalidOperationException("lock connection refused")));
        await using Rig rig = new(provider);

        await rig.StartAsync();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await rig.Clock.ReleaseNextAsync(PollInterval);
        }

        await Assert.That(rig.Service.ExecuteTask!.IsCompleted).IsFalse();
        await Assert.That(rig.CursorReads).IsEqualTo(0);
        await provider.Received(4).TryAcquireAsync(LockNames.KubernetesInventoryProjection, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Projector_CursorWriteAlwaysThrows_KeepsPollingAndDoesNotFault()
    {
        // The write that skips a row can fail too, and that failure surfaces from the row handler
        // rather than from the repository read, so it is a separate way for a poll to crash.
        await using Rig rig = new();
        int tenantId = await rig.SeedTenantAsync();
        long machineId = await rig.SeedMachineAsync(tenantId);
        await rig.SeedRowAsync(machineId, tenantId, "not json");
        rig.Repository.SetProjectionCursorAsync(Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("database unavailable")));

        await rig.StartAsync();
        for (int poll = 0; poll < 3; poll++)
        {
            await rig.Clock.ReleaseNextAsync(PollInterval);
        }

        await rig.Clock.NextDelayAsync();

        await Assert.That(rig.Service.ExecuteTask!.IsCompleted).IsFalse();
        await Assert.That(await rig.CursorAsync()).IsNull();
    }

    [Test]
    [Arguments("invalid_payload", 1)]
    [Arguments("apply_failed", 1)]
    [Arguments("retries_exhausted", 5)]
    public async Task Projector_SkippedRowIsCountedOnlyOnceItsCursorWriteSucceeds(string reason, int pollsBeforeTheWriteSucceeds)
    {
        // The skip's cursor write fails once. The row is then read and skipped again, and the counter
        // must record the one row once, not once per attempt to move the cursor past it.
        await using Rig rig = new();
        int tenantId = await rig.SeedTenantAsync();
        long machineId = await rig.SeedMachineAsync(tenantId);
        long row = await rig.SeedRowAsync(machineId, tenantId, reason == "invalid_payload" ? "not json" : NodePayload("node-a"));
        if (reason == "apply_failed")
        {
            rig.ApplyFault = (_, _) => new InvalidOperationException("constraint violated");
        }

        if (reason == "retries_exhausted")
        {
            rig.ApplyFault = (_, _) => new TimeoutException("connection reset");
        }

        int cursorWrites = 0;
        rig.Repository.SetProjectionCursorAsync(Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ci => Interlocked.Increment(ref cursorWrites) == 1
                ? Task.FromException(new InvalidOperationException("database unavailable"))
                : rig.Real.SetProjectionCursorAsync(ci.ArgAt<long>(0), ci.ArgAt<DateTimeOffset>(1), ci.ArgAt<CancellationToken>(2)));

        await rig.StartAsync();
        for (int poll = 0; poll < pollsBeforeTheWriteSucceeds; poll++)
        {
            ParkedDelay parked = await rig.Clock.NextDelayAsync();
            await Assert.That(parked.Delay).IsEqualTo(PollInterval);
            await Assert.That(rig.Failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
            await Assert.That(await rig.CursorAsync()).IsNull();

            rig.Clock.Advance(parked.Delay);
        }

        await rig.Clock.NextDelayAsync();

        await Assert.That(cursorWrites).IsEqualTo(2);
        await Assert.That(rig.FailureCount(reason)).IsEqualTo(1L);
        await Assert.That(rig.Failures.GetMeasurementSnapshot().Count).IsEqualTo(1);
        await Assert.That(await rig.CursorAsync()).IsEqualTo(row);
        await Assert.That(rig.Logger.Collector.GetSnapshot().Count(r => (r.Level == LogLevel.Error) && r.Message.Contains("was skipped")))
            .IsEqualTo(1);
    }

    [Test]
    public async Task Projector_RunsGcOnFirstPollThenHourly()
    {
        await using Rig rig = new();

        await rig.StartAsync();
        await rig.Clock.NextDelayAsync();

        await Assert.That(rig.GarbageCollections).IsEqualTo(1);

        rig.Clock.Advance(TimeSpan.FromMinutes(59));
        await rig.Clock.WaitUntilParkedPastAsync(Start.AddMinutes(59).Add(PollInterval));

        await Assert.That(rig.GarbageCollections).IsEqualTo(1);

        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        await rig.Clock.WaitUntilParkedPastAsync(Start.AddMinutes(61).Add(PollInterval));

        await Assert.That(rig.GarbageCollections).IsEqualTo(2);
    }

    [Test]
    public async Task Projector_GcFailure_IsLoggedAndDoesNotStopProjection()
    {
        await using Rig rig = new();
        int tenantId = await rig.SeedTenantAsync();
        long machineId = await rig.SeedMachineAsync(tenantId);
        long row = await rig.SeedRowAsync(machineId, tenantId, NodePayload("node-a"));
        rig.Repository.CollectGarbageAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new InvalidOperationException("deadlock detected")));

        await rig.StartAsync();
        await rig.Clock.NextDelayAsync();

        await Assert.That(await rig.CursorAsync()).IsEqualTo(row);
        await Assert.That(await rig.Db.Context.KubernetesNodes.CountAsync()).IsEqualTo(1);
        await Assert.That(rig.Logger.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Error)).IsEqualTo(1);
        await Assert.That(rig.Service.ExecuteTask!.IsCompleted).IsFalse();
    }

    [Test]
    public async Task Projector_TransientFailure_WaitsPollIntervalBeforeRetry()
    {
        await using Rig rig = new();
        int tenantId = await rig.SeedTenantAsync();
        long machineId = await rig.SeedMachineAsync(tenantId);
        long row = await rig.SeedRowAsync(machineId, tenantId, NodePayload("node-a"));
        rig.ApplyFault = (_, attempt) => attempt == 1 ? new TimeoutException("connection reset") : null;

        await rig.StartAsync();
        ParkedDelay afterFailure = await rig.Clock.NextDelayAsync();

        await Assert.That(afterFailure.Delay).IsEqualTo(PollInterval);

        rig.Clock.Advance(PollInterval - TimeSpan.FromMilliseconds(1));

        await Assert.That(rig.ApplyAttempts.Length).IsEqualTo(1);

        rig.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await rig.Clock.NextDelayAsync();

        await Assert.That(rig.ApplyAttempts.Length).IsEqualTo(2);
        await Assert.That(await rig.CursorAsync()).IsEqualTo(row);
    }

    [Test]
    public async Task Projector_LockHeldElsewhere_WaitsBeforeRetryingAndReadsNothing()
    {
        IAdvisoryLock handle = Substitute.For<IAdvisoryLock>();
        handle.IsAliveAsync(Arg.Any<CancellationToken>()).Returns(true);
        IAdvisoryLockProvider provider = Substitute.For<IAdvisoryLockProvider>();
        provider.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IAdvisoryLock?>(null), Task.FromResult<IAdvisoryLock?>(handle));
        await using Rig rig = new(provider);

        await rig.StartAsync();
        ParkedDelay waiting = await rig.Clock.NextDelayAsync();

        await Assert.That(waiting.Delay).IsEqualTo(LockRetryDelay);
        await Assert.That(rig.CursorReads).IsEqualTo(0);
        await Assert.That(rig.GarbageCollections).IsEqualTo(0);

        rig.Clock.Advance(waiting.Delay);
        ParkedDelay idle = await rig.Clock.NextDelayAsync();

        await Assert.That(idle.Delay).IsEqualTo(PollInterval);
        await Assert.That(rig.CursorReads).IsEqualTo(1);
        await provider.Received(2).TryAcquireAsync(LockNames.KubernetesInventoryProjection, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Projector_LockLost_ReleasesAndReacquiresThenCollectsAgain()
    {
        IAdvisoryLock first = Substitute.For<IAdvisoryLock>();
        first.IsAliveAsync(Arg.Any<CancellationToken>()).Returns(true, false);
        IAdvisoryLock second = Substitute.For<IAdvisoryLock>();
        second.IsAliveAsync(Arg.Any<CancellationToken>()).Returns(true);
        IAdvisoryLockProvider provider = Substitute.For<IAdvisoryLockProvider>();
        provider.TryAcquireAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IAdvisoryLock?>(first), Task.FromResult<IAdvisoryLock?>(second));
        await using Rig rig = new(provider);

        await rig.StartAsync();
        await rig.Clock.ReleaseNextAsync(PollInterval);
        await rig.Clock.NextDelayAsync();

        await Assert.That(rig.CursorReads).IsEqualTo(2);
        await Assert.That(rig.GarbageCollections).IsEqualTo(2);
        await first.Received(1).DisposeAsync();
        await provider.Received(2).TryAcquireAsync(LockNames.KubernetesInventoryProjection, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Projector_Stopping_IsNotCountedAsAFailure()
    {
        await using Rig rig = new();
        int tenantId = await rig.SeedTenantAsync();
        long machineId = await rig.SeedMachineAsync(tenantId);
        await rig.SeedRowAsync(machineId, tenantId, NodePayload("node-a"));
        TaskCompletionSource applying = new(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Repository.ApplyLocalNodeReportAsync(Arg.Any<LocalNodeReport>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                applying.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ci.ArgAt<CancellationToken>(2));

                return LocalReportOutcome.Grouped;
            });

        await rig.StartAsync();
        await applying.Task;
        await rig.Service.StopAsync(CancellationToken.None);

        await Assert.That(rig.Service.ExecuteTask!.IsCompletedSuccessfully).IsTrue();
        await Assert.That(rig.Failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
        await Assert.That(rig.Logger.Collector.GetSnapshot().Count(r => r.Level == LogLevel.Error)).IsEqualTo(0);
        await Assert.That(await rig.CursorAsync()).IsNull();
    }

    private static MachineTelemetry TelemetryRow()
    {
        return new MachineTelemetry
        {
            Id = 77,
            MachineId = 9,
            TenantId = 3,
            TelemetryType = 14,
            Payload = "{}",
            ReceivedAt = Start,
            ServerReceivedAt = Start.AddSeconds(2),
            SourceEventId = "event-77",
        };
    }

    private static KubernetesNodePayload ValidPayload()
    {
        return new KubernetesNodePayload
        {
            Distro = "k3s",
            KubeletVersion = "v1.30.4+k3s1",
            NodeName = "node-1",
            NodeNameSource = "certificate",
            HasKubelet = true,
            ControlPlane = true,
            CaFingerprint = FingerprintA,
        };
    }

    [Test]
    public async Task NormalisePayload_ValidPayload_MapsEveryField()
    {
        LocalNodeReport? report = KubernetesInventoryProjectionService.NormalisePayload(ValidPayload(), TelemetryRow());

        await Assert.That(report).IsEqualTo(new LocalNodeReport(
            TelemetryId: 77,
            TenantId: 3,
            MachineId: 9,
            ServerReceivedAt: Start.AddSeconds(2),
            Distro: "k3s",
            NodeName: "node-1",
            HasKubelet: true,
            ControlPlane: true,
            CaFingerprint: FingerprintA,
            KubeletVersion: "v1.30.4+k3s1"));
    }

    [Test]
    [Arguments("k3s")]
    [Arguments("rke2")]
    [Arguments("kubeadm")]
    [Arguments("k0s")]
    [Arguments("microk8s")]
    [Arguments("unknown")]
    public async Task NormalisePayload_KnownDistro_IsKept(string distro)
    {
        KubernetesNodePayload payload = ValidPayload();
        payload.Distro = distro;

        LocalNodeReport? report = KubernetesInventoryProjectionService.NormalisePayload(payload, TelemetryRow());

        await Assert.That(report!.Distro).IsEqualTo(distro);
    }

    [Test]
    [Arguments("docker")]
    [Arguments("K3S")]
    [Arguments("k3s ")]
    [Arguments(" ")]
    public async Task NormalisePayload_UnknownDistro_BecomesUnknown(string distro)
    {
        KubernetesNodePayload payload = ValidPayload();
        payload.Distro = distro;

        LocalNodeReport? report = KubernetesInventoryProjectionService.NormalisePayload(payload, TelemetryRow());

        await Assert.That(report!.Distro).IsEqualTo("unknown");
    }

    [Test]
    [Arguments("")]
    [Arguments(null)]
    public async Task NormalisePayload_EmptyDistro_IsRejected(string? distro)
    {
        // An empty distro is how a payload the server failed to serialise reads back, as "{}", so it
        // is counted as malformed rather than recorded as a host that is not a node.
        KubernetesNodePayload payload = ValidPayload();
        payload.Distro = distro!;

        await Assert.That(KubernetesInventoryProjectionService.NormalisePayload(payload, TelemetryRow())).IsNull();
    }

    [Test]
    public async Task NormalisePayload_EmptyPayload_IsRejected()
    {
        await Assert.That(KubernetesInventoryProjectionService.NormalisePayload(new KubernetesNodePayload(), TelemetryRow())).IsNull();
    }

    [Test]
    [Arguments("A1B2C3D4E5F60718A1B2C3D4E5F60718A1B2C3D4E5F60718A1B2C3D4E5F60718")]
    [Arguments("a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f6071")]
    [Arguments("a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f607180")]
    [Arguments("g1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718")]
    [Arguments(" a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f60718a1b2c3d4e5f6071")]
    [Arguments("")]
    [Arguments(null)]
    public async Task NormalisePayload_BadFingerprint_BecomesNull(string? fingerprint)
    {
        KubernetesNodePayload payload = ValidPayload();
        payload.CaFingerprint = fingerprint!;

        LocalNodeReport? report = KubernetesInventoryProjectionService.NormalisePayload(payload, TelemetryRow());

        await Assert.That(report).IsNotNull();
        await Assert.That(report!.CaFingerprint).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments(null)]
    public async Task NormalisePayload_EmptyKubeletVersion_BecomesNull(string? version)
    {
        KubernetesNodePayload payload = ValidPayload();
        payload.KubeletVersion = version!;

        LocalNodeReport? report = KubernetesInventoryProjectionService.NormalisePayload(payload, TelemetryRow());

        await Assert.That(report!.KubeletVersion).IsNull();
    }

    [Test]
    public async Task NormalisePayload_OverLongKubeletVersion_IsTruncatedTo64()
    {
        KubernetesNodePayload payload = ValidPayload();
        payload.KubeletVersion = "v" + new string('1', 200);

        LocalNodeReport? report = KubernetesInventoryProjectionService.NormalisePayload(payload, TelemetryRow());

        await Assert.That(report!.KubeletVersion).IsEqualTo(payload.KubeletVersion[..64]);
    }

    [Test]
    public async Task NormalisePayload_KubeletVersionOfExactly64_IsKept()
    {
        KubernetesNodePayload payload = ValidPayload();
        payload.KubeletVersion = "v" + new string('1', 63);

        LocalNodeReport? report = KubernetesInventoryProjectionService.NormalisePayload(payload, TelemetryRow());

        await Assert.That(report!.KubeletVersion).IsEqualTo(payload.KubeletVersion);
    }

    [Test]
    public async Task NormalisePayload_TruncationNeverSplitsASurrogatePair()
    {
        // A lone surrogate cannot be encoded as UTF-8, so cutting a pair in half would turn a long
        // version into a value the database rejects on every attempt.
        KubernetesNodePayload payload = ValidPayload();
        payload.KubeletVersion = new string('v', 63) + "\U0001F600" + "tail";

        LocalNodeReport? report = KubernetesInventoryProjectionService.NormalisePayload(payload, TelemetryRow());

        await Assert.That(report!.KubeletVersion).IsEqualTo(new string('v', 63));
    }

    [Test]
    [Arguments("Node-1")]
    [Arguments("-node")]
    [Arguments("node-")]
    [Arguments("node..one")]
    [Arguments(".node")]
    [Arguments("node.")]
    [Arguments("node_1")]
    [Arguments("node name")]
    [Arguments("node\n")]
    [Arguments("nöde")]
    [Arguments("a-.b")]
    public async Task NormalisePayload_InvalidNodeNameWithKubelet_IsRejected(string nodeName)
    {
        KubernetesNodePayload payload = ValidPayload();
        payload.NodeName = nodeName;

        await Assert.That(KubernetesInventoryProjectionService.NormalisePayload(payload, TelemetryRow())).IsNull();
    }

    [Test]
    public async Task NormalisePayload_OverLongNodeNameWithKubelet_IsRejected()
    {
        KubernetesNodePayload payload = ValidPayload();
        payload.NodeName = new string('a', 254);

        await Assert.That(KubernetesInventoryProjectionService.NormalisePayload(payload, TelemetryRow())).IsNull();
    }

    [Test]
    public async Task NormalisePayload_NodeNameOf253_IsKept()
    {
        KubernetesNodePayload payload = ValidPayload();
        payload.NodeName = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));

        LocalNodeReport? report = KubernetesInventoryProjectionService.NormalisePayload(payload, TelemetryRow());

        await Assert.That(payload.NodeName.Length).IsEqualTo(253);
        await Assert.That(report!.NodeName).IsEqualTo(payload.NodeName);
    }

    [Test]
    [Arguments("")]
    [Arguments(null)]
    public async Task NormalisePayload_EmptyNodeNameWithKubelet_IsRejected(string? nodeName)
    {
        KubernetesNodePayload payload = ValidPayload();
        payload.NodeName = nodeName!;

        await Assert.That(KubernetesInventoryProjectionService.NormalisePayload(payload, TelemetryRow())).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments(null)]
    public async Task NormalisePayload_EmptyNodeNameWithoutKubelet_PassesThrough(string? nodeName)
    {
        KubernetesNodePayload payload = ValidPayload();
        payload.HasKubelet = false;
        payload.NodeName = nodeName!;

        LocalNodeReport? report = KubernetesInventoryProjectionService.NormalisePayload(payload, TelemetryRow());

        await Assert.That(report).IsNotNull();
        await Assert.That(report!.HasKubelet).IsFalse();
        await Assert.That(report.NodeName).IsEqualTo("");
    }

    [Test]
    [Arguments("Node_1")]
    [Arguments("-node")]
    [Arguments("node name")]
    public async Task NormalisePayload_InvalidNodeNameWithoutKubelet_IsRejected(string nodeName)
    {
        // Only an empty name is excused for a host without a kubelet; a name that is present has to be
        // a real one.
        KubernetesNodePayload payload = ValidPayload();
        payload.HasKubelet = false;
        payload.NodeName = nodeName;

        await Assert.That(KubernetesInventoryProjectionService.NormalisePayload(payload, TelemetryRow())).IsNull();
    }

    [Test]
    public async Task NormalisePayload_OverLongNodeNameWithoutKubelet_IsRejected()
    {
        KubernetesNodePayload payload = ValidPayload();
        payload.HasKubelet = false;
        payload.NodeName = new string('a', 254);

        await Assert.That(KubernetesInventoryProjectionService.NormalisePayload(payload, TelemetryRow())).IsNull();
    }

    [Test]
    public async Task NormalisePayload_ValidNodeNameWithoutKubelet_IsKept()
    {
        KubernetesNodePayload payload = ValidPayload();
        payload.HasKubelet = false;

        LocalNodeReport? report = KubernetesInventoryProjectionService.NormalisePayload(payload, TelemetryRow());

        await Assert.That(report!.HasKubelet).IsFalse();
        await Assert.That(report.NodeName).IsEqualTo("node-1");
    }
}
