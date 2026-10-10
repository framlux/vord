// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Pins the quota pause series. A check that cannot be made fails open, so the failure counter is
/// the only signal that a fleet is running unmetered; it has to carry the surface that asked and
/// has to exist before the first failure.
/// </summary>
public sealed class QuotaPauseMetricsTests
{
    private static (QuotaPauseMetrics Metrics, IMeterFactory Factory) Build()
    {
        ServiceCollection services = new();
        services.AddMetrics();
        ServiceProvider provider = services.BuildServiceProvider();
        IMeterFactory factory = provider.GetRequiredService<IMeterFactory>();

        return (new QuotaPauseMetrics(factory), factory);
    }

    [Test]
    public async Task RecordCheckFailure_RecordsOneMeasurementTaggedWithTheSurface()
    {
        (QuotaPauseMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.quota.pause_check_failures");

        metrics.RecordCheckFailure(QuotaCheckSurface.Ingest);

        IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(1L);
        await Assert.That(measurements[0].Tags["surface"]).IsEqualTo("ingest");
    }

    [Test]
    [Arguments(QuotaCheckSurface.Ingest, "ingest")]
    [Arguments(QuotaCheckSurface.Ping, "ping")]
    [Arguments(QuotaCheckSurface.Commands, "commands")]
    [Arguments(QuotaCheckSurface.Display, "display")]
    [Arguments(QuotaCheckSurface.AlertEvaluation, "alert_evaluation")]
    public async Task RecordCheckFailure_EverySurface_IsSnakeCased(QuotaCheckSurface surface, string expected)
    {
        (QuotaPauseMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.quota.pause_check_failures");

        metrics.RecordCheckFailure(surface);

        await Assert.That(failures.GetMeasurementSnapshot()[0].Tags["surface"]).IsEqualTo(expected);
    }

    [Test]
    public async Task InitialiseSeries_RecordsAZeroForEverySurface()
    {
        (QuotaPauseMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.quota.pause_check_failures");

        metrics.InitialiseSeries();

        IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(Enum.GetValues<QuotaCheckSurface>().Length);
        await Assert.That(measurements.All(measurement => measurement.Value == 0L)).IsTrue();

        string[] surfaces = [.. measurements.Select(measurement => (string)measurement.Tags["surface"]!)];
        await Assert.That(surfaces.Order().ToArray()).IsEquivalentTo(new[] { "alert_evaluation", "commands", "display", "ingest", "ping" });
    }
}
