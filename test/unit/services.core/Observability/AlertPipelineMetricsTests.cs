// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Pins the alert-firing series: what fired, of what severity, on which metric. Deliberately not a
/// funnel, and deliberately not a liveness signal — zero firings is the healthy state, so this
/// answers "what happened during the incident", not "is the pipeline alive".
/// </summary>
public sealed class AlertPipelineMetricsTests
{
    private static (AlertPipelineMetrics Metrics, IMeterFactory Factory) Build()
    {
        ServiceCollection services = new();
        services.AddMetrics();
        ServiceProvider provider = services.BuildServiceProvider();
        IMeterFactory factory = provider.GetRequiredService<IMeterFactory>();

        return (new AlertPipelineMetrics(factory), factory);
    }

    [Test]
    public async Task RecordEventFired_TagsMetricAndSeverity()
    {
        (AlertPipelineMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.alerts.events");

        metrics.RecordEventFired(AlertMetric.SshConnection, AlertSeverity.Critical);

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["metric"]).IsEqualTo("ssh_connection");
        await Assert.That(measurements[0].Tags["severity"]).IsEqualTo("critical");
    }

    [Test]
    [Arguments(AlertMetric.CpuUsage, "cpu_usage")]
    [Arguments(AlertMetric.MachineOffline, "machine_offline")]
    [Arguments(AlertMetric.SecurityUpdates, "security_updates")]
    [Arguments(AlertMetric.TelemetryStale, "telemetry_stale")]
    public async Task RecordEventFired_MultiWordMetricsAreSnakeCased(AlertMetric metric, string expected)
    {
        (AlertPipelineMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.alerts.events");

        metrics.RecordEventFired(metric, AlertSeverity.Warning);

        await Assert.That(collector.GetMeasurementSnapshot()[0].Tags["metric"]).IsEqualTo(expected);
    }

    [Test]
    public async Task RecordEventFired_CarriesNoTenantTag()
    {
        // Tenants by metrics by severities is the combination that actually runs away.
        (AlertPipelineMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.alerts.events");

        metrics.RecordEventFired(AlertMetric.DiskUsage, AlertSeverity.Info);

        await Assert.That(collector.GetMeasurementSnapshot()[0].Tags.ContainsKey("tenant")).IsFalse();
    }

    [Test]
    public async Task InitialiseSeries_CreatesEveryMetricAndSeverityCombination()
    {
        (AlertPipelineMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.alerts.events");

        metrics.InitialiseSeries();

        // Nine metrics by three severities.
        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(27);
        await Assert.That(measurements.All(measurement => measurement.Value == 0L)).IsTrue();
    }
}
