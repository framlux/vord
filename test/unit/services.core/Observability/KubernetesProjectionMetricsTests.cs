// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Kubernetes;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Pins the Kubernetes projector lag gauge. A caught-up projector reports zero rather than the
/// ever-growing age of whatever it last handled, the age is measured on the injected clock against the
/// same rows the projector will read, and a storage failure yields no measurement rather than an
/// exception that would take the whole collection cycle with it.
/// </summary>
public sealed class KubernetesProjectionMetricsTests
{
    private const string Instrument = "vord.kubernetes.projection.lag_seconds";

    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    private static (KubernetesProjectionMetrics Metrics, IMeterFactory Factory, FakeTimeProvider Clock) Build(
        IKubernetesInventoryRepository repository, int visibilityLagSeconds = 5)
    {
        IMeterFactory factory = TestMetricsFactory.CreateMeterFactory();

        // The substituted repository is served from the additional-services dictionary, which
        // TestServiceScopeFactory checks before it ever reaches for the context, so no database is
        // needed here.
        TestServiceScopeFactory scopeFactory = new(
            null!,
            new Dictionary<Type, object> { [typeof(IKubernetesInventoryRepository)] = repository });
        FakeTimeProvider clock = new(Now);

        KubernetesProjectionMetrics metrics = new(
            factory,
            scopeFactory,
            clock,
            Options.Create(new StreamingOptions { VisibilityLagSeconds = visibilityLagSeconds }),
            NullLogger<KubernetesProjectionMetrics>.Instance);

        return (metrics, factory, clock);
    }

    private static IKubernetesInventoryRepository RepositoryWithOldest(DateTimeOffset? oldest, long? cursor = 100)
    {
        IKubernetesInventoryRepository repository = Substitute.For<IKubernetesInventoryRepository>();
        repository.GetProjectionCursorAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(cursor));
        repository.GetOldestUnprojectedKubernetesReceiptAsync(
                Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(oldest));

        return repository;
    }

    [Test]
    public async Task Gauge_ProjectorBehind_ReportsTheAgeOfTheOldestUnprojectedRowOnTheInjectedClock()
    {
        IKubernetesInventoryRepository repository = RepositoryWithOldest(Now.AddSeconds(-120));
        (KubernetesProjectionMetrics _, IMeterFactory factory, FakeTimeProvider clock) = Build(repository);
        using MetricCollector<double> collector = new(factory, VordMeter.Name, Instrument);

        collector.RecordObservableInstruments();
        double first = collector.LastMeasurement!.Value;
        clock.Advance(TimeSpan.FromSeconds(30));
        collector.RecordObservableInstruments();
        double second = collector.LastMeasurement!.Value;

        await Assert.That(first).IsEqualTo(120d);
        await Assert.That(second).IsEqualTo(150d);
    }

    [Test]
    public async Task Gauge_NothingUnprojected_ReportsZero()
    {
        // An idle fleet has nothing outstanding. Reporting the age of the last projected row here would
        // make a healthy projector look further behind every minute.
        IKubernetesInventoryRepository repository = RepositoryWithOldest(null);
        (KubernetesProjectionMetrics _, IMeterFactory factory, FakeTimeProvider _) = Build(repository);
        using MetricCollector<double> collector = new(factory, VordMeter.Name, Instrument);

        collector.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<double>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(0d);
    }

    [Test]
    public async Task Gauge_IsASingleUntaggedSeriesInSeconds()
    {
        // The lag is fleet-wide: the projector is one stream under one lock, so a shard or tenant tag
        // would only multiply series without telling an operator anything the single one does not.
        IKubernetesInventoryRepository repository = RepositoryWithOldest(Now.AddSeconds(-10));
        (KubernetesProjectionMetrics _, IMeterFactory factory, FakeTimeProvider _) = Build(repository);
        using MetricCollector<double> collector = new(factory, VordMeter.Name, Instrument);

        collector.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<double>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags.Count).IsEqualTo(0);
        await Assert.That(collector.Instrument!.Unit).IsEqualTo("s");
    }

    [Test]
    public async Task Gauge_ReadsTheRowsTheProjectorWouldRead()
    {
        // A row the projector never reads is not lag, so the query must carry the cursor, the projector's
        // own read window and its visibility lag, all measured from the same instant.
        IKubernetesInventoryRepository repository = RepositoryWithOldest(Now.AddSeconds(-60), cursor: 4242);
        (KubernetesProjectionMetrics _, IMeterFactory factory, FakeTimeProvider _) = Build(repository, visibilityLagSeconds: 45);
        using MetricCollector<double> collector = new(factory, VordMeter.Name, Instrument);

        collector.RecordObservableInstruments();

        await repository.Received(1).GetOldestUnprojectedKubernetesReceiptAsync(
            4242,
            Now - KubernetesInventoryProjectionService.ReadWindow,
            Now.AddSeconds(-45),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Gauge_NoCursorYet_ReadsFromTheStart()
    {
        // Before the projector has handled anything its cursor row does not exist, which is position
        // zero, the same as the projector itself reads it.
        IKubernetesInventoryRepository repository = RepositoryWithOldest(Now.AddSeconds(-60), cursor: null);
        (KubernetesProjectionMetrics _, IMeterFactory factory, FakeTimeProvider _) = Build(repository);
        using MetricCollector<double> collector = new(factory, VordMeter.Name, Instrument);

        collector.RecordObservableInstruments();

        await repository.Received(1).GetOldestUnprojectedKubernetesReceiptAsync(
            0, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Gauge_NegativeVisibilityLag_IsReadAsNone()
    {
        // The projector clamps a negative lag to zero, so the gauge must measure the row set it reads
        // rather than one reaching into the future.
        IKubernetesInventoryRepository repository = RepositoryWithOldest(Now.AddSeconds(-60));
        (KubernetesProjectionMetrics _, IMeterFactory factory, FakeTimeProvider _) = Build(repository, visibilityLagSeconds: -30);
        using MetricCollector<double> collector = new(factory, VordMeter.Name, Instrument);

        collector.RecordObservableInstruments();

        await repository.Received(1).GetOldestUnprojectedKubernetesReceiptAsync(
            Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Now, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Gauge_ReceiptAheadOfTheClock_ReportsZeroNotNegative()
    {
        // A row can carry a receipt time a little ahead of this process's clock when the clocks differ;
        // a negative lag would read as nonsense on a dashboard.
        IKubernetesInventoryRepository repository = RepositoryWithOldest(Now.AddSeconds(8));
        (KubernetesProjectionMetrics _, IMeterFactory factory, FakeTimeProvider _) = Build(repository);
        using MetricCollector<double> collector = new(factory, VordMeter.Name, Instrument);

        collector.RecordObservableInstruments();

        await Assert.That(collector.LastMeasurement!.Value).IsEqualTo(0d);
    }

    [Test]
    public async Task Gauge_CursorReadThrows_YieldsNoMeasurement()
    {
        // An exception inside an observable callback aborts the whole collection cycle, so one
        // unhealthy query would blind every other instrument in the process.
        IKubernetesInventoryRepository repository = Substitute.For<IKubernetesInventoryRepository>();
        repository.GetProjectionCursorAsync(Arg.Any<CancellationToken>())
            .Returns<Task<long?>>(_ => throw new InvalidOperationException("database unavailable"));
        (KubernetesProjectionMetrics _, IMeterFactory factory, FakeTimeProvider _) = Build(repository);
        using MetricCollector<double> collector = new(factory, VordMeter.Name, Instrument);

        collector.RecordObservableInstruments();

        await Assert.That(collector.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Gauge_OldestRowReadThrows_YieldsNoMeasurement()
    {
        // A measurement of zero here would claim a caught-up projector at the moment its backlog
        // cannot even be read, so a failure must omit the series instead.
        IKubernetesInventoryRepository repository = Substitute.For<IKubernetesInventoryRepository>();
        repository.GetProjectionCursorAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(100));
        repository.GetOldestUnprojectedKubernetesReceiptAsync(
                Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns<Task<DateTimeOffset?>>(_ => throw new InvalidOperationException("database unavailable"));
        (KubernetesProjectionMetrics _, IMeterFactory factory, FakeTimeProvider _) = Build(repository);
        using MetricCollector<double> collector = new(factory, VordMeter.Name, Instrument);

        collector.RecordObservableInstruments();

        await Assert.That(collector.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Gauge_ReadIsBoundedByACancellableToken_AndItsCancellationYieldsNoMeasurement()
    {
        // The callback runs on the exporter's collection thread, so a query that hangs must be
        // abandoned by a timeout rather than stall every instrument in the process. The wait itself is
        // the database driver's cancellation, so what is pinned here is that a token which can be
        // cancelled reaches the query and that its cancellation is swallowed like any other failure.
        CancellationToken observed = default;
        IKubernetesInventoryRepository repository = Substitute.For<IKubernetesInventoryRepository>();
        repository.GetProjectionCursorAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(100));
        repository.GetOldestUnprojectedKubernetesReceiptAsync(
                Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Do<CancellationToken>(token => observed = token))
            .Returns<Task<DateTimeOffset?>>(_ => throw new OperationCanceledException());
        (KubernetesProjectionMetrics _, IMeterFactory factory, FakeTimeProvider _) = Build(repository);
        using MetricCollector<double> collector = new(factory, VordMeter.Name, Instrument);

        collector.RecordObservableInstruments();

        await Assert.That(observed.CanBeCanceled).IsTrue();
        await Assert.That(collector.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }
}
