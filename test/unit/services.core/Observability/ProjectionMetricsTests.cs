// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
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
/// Pins the projection lag gauge. Three properties matter: a caught-up shard reports zero rather
/// than an ever-growing age, a storage failure yields no measurement rather than an exception that
/// would take the whole collection cycle with it, and each tracked shard reports its own series.
/// </summary>
public sealed class ProjectionMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static (ProjectionMetrics Metrics, IMeterFactory Factory) Build(IMachineStateRepository repository)
    {
        IMeterFactory factory = TestMetricsFactory.CreateMeterFactory();

        // The substituted repository is served from the additional-services dictionary, which
        // TestServiceScopeFactory checks before it ever reaches for the context, so no database is
        // needed here. Migrating a throwaway SQLite file per test would be pure cost.
        TestServiceScopeFactory scopeFactory = new(
            null!,
            new Dictionary<Type, object> { [typeof(IMachineStateRepository)] = repository });

        ProjectionMetrics metrics = new(
            factory,
            scopeFactory,
            new FakeTimeProvider(Now),
            Options.Create(new StreamingOptions { ShardCount = 2 }),
            NullLogger<ProjectionMetrics>.Instance);

        return (metrics, factory);
    }

    [Test]
    public async Task Gauge_CaughtUpShard_ReportsZero()
    {
        // An idle fleet has nothing outstanding. Reporting the age of the last processed row here
        // would make a perfectly healthy projection look further behind every minute.
        IMachineStateRepository repository = Substitute.For<IMachineStateRepository>();
        repository.GetProjectionCursorAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(100));
        repository.GetOldestUnprojectedReceiptAsync(
                Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DateTimeOffset?>(null));

        (ProjectionMetrics metrics, IMeterFactory factory) = Build(repository);
        metrics.TrackShard(0);
        using MetricCollector<double> collector = new(factory, VordMeter.Name, "vord.projection.lag_seconds");

        collector.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<double>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(0d);
    }

    [Test]
    public async Task Gauge_BacklogPresent_ReportsTheAgeOfTheOldestUnprojectedRow()
    {
        IMachineStateRepository repository = Substitute.For<IMachineStateRepository>();
        repository.GetProjectionCursorAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(100));
        repository.GetOldestUnprojectedReceiptAsync(
                Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DateTimeOffset?>(Now.AddSeconds(-120)));

        (ProjectionMetrics metrics, IMeterFactory factory) = Build(repository);
        metrics.TrackShard(0);
        using MetricCollector<double> collector = new(factory, VordMeter.Name, "vord.projection.lag_seconds");

        collector.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<double>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements[0].Value).IsEqualTo(120d);
        await Assert.That(measurements[0].Tags["shard_index"]).IsEqualTo("0");
    }

    [Test]
    public async Task Gauge_StorageThrows_YieldsNoMeasurement()
    {
        // An exception inside an observable callback aborts the whole collection cycle, so one
        // unhealthy query would blind every other instrument in the process.
        IMachineStateRepository repository = Substitute.For<IMachineStateRepository>();
        repository.GetProjectionCursorAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<long?>>(_ => throw new InvalidOperationException("database unavailable"));

        (ProjectionMetrics metrics, IMeterFactory factory) = Build(repository);
        metrics.TrackShard(0);
        using MetricCollector<double> collector = new(factory, VordMeter.Name, "vord.projection.lag_seconds");

        collector.RecordObservableInstruments();

        await Assert.That(collector.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task Gauge_TwoTrackedShards_ReportOneSeriesEach()
    {
        // Two replicas behind advisory locks project different shards; an unsharded gauge would
        // have them overwrite each other's notion of lag.
        IMachineStateRepository repository = Substitute.For<IMachineStateRepository>();
        repository.GetProjectionCursorAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<long?>(0));
        repository.GetOldestUnprojectedReceiptAsync(
                Arg.Any<long>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DateTimeOffset?>(null));

        (ProjectionMetrics metrics, IMeterFactory factory) = Build(repository);
        metrics.TrackShard(0);
        metrics.TrackShard(1);
        using MetricCollector<double> collector = new(factory, VordMeter.Name, "vord.projection.lag_seconds");

        collector.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<double>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(2);
        await Assert.That(measurements
            .Select(measurement => (string?)measurement.Tags["shard_index"])
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList()).IsEquivalentTo(new List<string?> { "0", "1" });
    }
}
