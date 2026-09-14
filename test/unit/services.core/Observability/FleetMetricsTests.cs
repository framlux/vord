// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Pins the fleet gauge. Its explicit zeros are not cosmetic: the ingest-stalled rule is gated on
/// this series, so a status that reports nothing instead of zero turns that rule into one that can
/// never fire.
/// </summary>
public sealed class FleetMetricsTests
{
    private static (FleetMetrics Metrics, IMeterFactory Factory) Build(IMachineStateRepository repository)
    {
        IMeterFactory factory = TestMetricsFactory.CreateMeterFactory();

        // The substituted repository is served from the additional-services dictionary before the
        // context is ever reached, so no database is needed here.
        TestServiceScopeFactory scopeFactory = new(
            null!,
            new Dictionary<Type, object> { [typeof(IMachineStateRepository)] = repository });

        return (new FleetMetrics(factory, scopeFactory, NullLogger<FleetMetrics>.Instance), factory);
    }

    [Test]
    public async Task Gauge_EmitsOneSeriesPerStatusEvenWithNoMachines()
    {
        // Production has no tenants today. A gauge that reported nothing here would leave the
        // ingest rule's gate with no series to evaluate against.
        IMachineStateRepository repository = Substitute.For<IMachineStateRepository>();
        repository.GetFleetMachineCountsByHealthAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<short, int>>(new Dictionary<short, int>()));

        (FleetMetrics metrics, IMeterFactory factory) = Build(repository);
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.fleet.machines");

        collector.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(4);
        await Assert.That(measurements.All(measurement => measurement.Value == 0L)).IsTrue();
        await Assert.That(metrics).IsNotNull();
    }

    [Test]
    public async Task Gauge_ReportsTheCountForEachStatus()
    {
        IMachineStateRepository repository = Substitute.For<IMachineStateRepository>();
        repository.GetFleetMachineCountsByHealthAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<short, int>>(new Dictionary<short, int>
            {
                [(short)MachineHealthStatus.Healthy] = 3,
                [(short)MachineHealthStatus.Offline] = 1,
            }));

        (FleetMetrics metrics, IMeterFactory factory) = Build(repository);
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.fleet.machines");

        collector.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        CollectedMeasurement<long> healthy = measurements.Single(m => (string?)m.Tags["status"] == "healthy");
        CollectedMeasurement<long> offline = measurements.Single(m => (string?)m.Tags["status"] == "offline");
        CollectedMeasurement<long> warning = measurements.Single(m => (string?)m.Tags["status"] == "warning");

        await Assert.That(healthy.Value).IsEqualTo(3L);
        await Assert.That(offline.Value).IsEqualTo(1L);
        await Assert.That(warning.Value).IsEqualTo(0L);
        await Assert.That(metrics).IsNotNull();
    }

    [Test]
    public async Task Gauge_WhenStorageThrows_YieldsNoMeasurement()
    {
        IMachineStateRepository repository = Substitute.For<IMachineStateRepository>();
        repository.GetFleetMachineCountsByHealthAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyDictionary<short, int>>>(_ => throw new InvalidOperationException("database unavailable"));

        (FleetMetrics metrics, IMeterFactory factory) = Build(repository);
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.fleet.machines");

        collector.RecordObservableInstruments();

        await Assert.That(collector.GetMeasurementSnapshot().Count).IsEqualTo(0);
        await Assert.That(metrics).IsNotNull();
    }
}
