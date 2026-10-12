// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Pins the Kubernetes projection failure series. A row the projector skips is lost from the
/// inventory for good, so this counter is the only evidence that it happened; the reason tag must
/// match what an alert rule is written against, and the series must exist before the first failure.
/// </summary>
public sealed class KubernetesInventoryMetricsTests
{
    private const string Instrument = "vord.kubernetes.projection_failures";

    private static (KubernetesInventoryMetrics Metrics, IMeterFactory Factory) Build()
    {
        ServiceCollection services = new();
        services.AddMetrics();
        ServiceProvider provider = services.BuildServiceProvider();
        IMeterFactory factory = provider.GetRequiredService<IMeterFactory>();

        return (new KubernetesInventoryMetrics(factory), factory);
    }

    [Test]
    [Arguments(KubernetesProjectionFailureReason.InvalidPayload, "invalid_payload")]
    [Arguments(KubernetesProjectionFailureReason.RetriesExhausted, "retries_exhausted")]
    [Arguments(KubernetesProjectionFailureReason.ApplyFailed, "apply_failed")]
    public async Task RecordProjectionFailure_RecordsOneMeasurementTaggedWithTheSnakeCasedReason(
        KubernetesProjectionFailureReason reason, string expected)
    {
        (KubernetesInventoryMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> failures = new(factory, VordMeter.Name, Instrument);

        metrics.RecordProjectionFailure(reason);

        IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(1L);
        await Assert.That(measurements[0].Tags["reason"]).IsEqualTo(expected);
        await Assert.That(measurements[0].Tags.Count).IsEqualTo(1);
    }

    [Test]
    public async Task InitialiseSeries_RecordsAZeroForEveryReason()
    {
        (KubernetesInventoryMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> failures = new(factory, VordMeter.Name, Instrument);

        metrics.InitialiseSeries();

        IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(Enum.GetValues<KubernetesProjectionFailureReason>().Length);
        await Assert.That(measurements.All(measurement => measurement.Value == 0L)).IsTrue();

        string[] reasons = [.. measurements.Select(measurement => (string)measurement.Tags["reason"]!)];
        await Assert.That(reasons.Order().ToArray()).IsEquivalentTo(new[] { "apply_failed", "invalid_payload", "retries_exhausted" });
    }
}
