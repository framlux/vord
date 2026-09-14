// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Pins the fail-open series. Both counters it replaces were recorded correctly and thrown away,
/// because nothing subscribed to the meters they were created on — the failure this whole work item
/// exists to end.
/// </summary>
public sealed class ResilienceMetricsTests
{
    private static (ResilienceMetrics Metrics, IMeterFactory Factory) Build()
    {
        ServiceCollection services = new();
        services.AddMetrics();
        ServiceProvider provider = services.BuildServiceProvider();
        IMeterFactory factory = provider.GetRequiredService<IMeterFactory>();

        return (new ResilienceMetrics(factory), factory);
    }

    [Test]
    [Arguments(ResilienceComponent.RateLimiter, "rate_limiter")]
    [Arguments(ResilienceComponent.TelemetryDedup, "telemetry_dedup")]
    public async Task RecordFailOpen_TagsTheComponent(ResilienceComponent component, string expected)
    {
        (ResilienceMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.redis.fail_open");

        metrics.RecordFailOpen(component);

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["component"]).IsEqualTo(expected);
    }

    [Test]
    public async Task InstrumentName_HasNoTotalSuffix()
    {
        // The collector suppresses type suffixes, so a name written with one here would produce a
        // series no rule matches. Collecting on the exact name and seeing a measurement proves it.
        (ResilienceMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.redis.fail_open");

        metrics.RecordFailOpen(ResilienceComponent.TelemetryDedup);

        await Assert.That(collector.GetMeasurementSnapshot().Count).IsEqualTo(1);
    }

    [Test]
    public async Task InitialiseSeries_CreatesBothComponents()
    {
        (ResilienceMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.redis.fail_open");

        metrics.InitialiseSeries();

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(2);
        await Assert.That(measurements.All(measurement => measurement.Value == 0L)).IsTrue();
    }
}
