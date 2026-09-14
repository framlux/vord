// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Asserts the exact series these instruments produce — names and tag values, not just that a
/// counter moved. The names are a contract with alert rules in another repository, and a mismatch
/// there is silent, so the spelling is the thing worth testing.
/// </summary>
public sealed class IngestMetricsTests
{
    private static (IngestMetrics Metrics, IMeterFactory Factory) Build()
    {
        ServiceCollection services = new();
        services.AddMetrics();
        ServiceProvider provider = services.BuildServiceProvider();
        IMeterFactory factory = provider.GetRequiredService<IMeterFactory>();

        return (new IngestMetrics(factory), factory);
    }

    [Test]
    public async Task RecordEnvelope_EmitsTheAcceptedOutcomeWithNoTotalSuffix()
    {
        (IngestMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.ingest.envelopes");

        metrics.RecordEnvelope(IngestOutcome.Accepted);

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(1L);
        await Assert.That(measurements[0].Tags["outcome"]).IsEqualTo("accepted");
    }

    [Test]
    public async Task RecordEnvelope_UnavailableIsDistinctFromRejected()
    {
        // A database outage and a malformed envelope are different incidents with different
        // responses, and only one of them is the agent's fault. They must not share a bucket.
        (IngestMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.ingest.envelopes");

        metrics.RecordEnvelope(IngestOutcome.Unavailable);
        metrics.RecordEnvelope(IngestOutcome.NotEntitled);
        metrics.RecordEnvelope(IngestOutcome.Rejected);

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements[0].Tags["outcome"]).IsEqualTo("unavailable");
        await Assert.That(measurements[1].Tags["outcome"]).IsEqualTo("not_entitled");
        await Assert.That(measurements[2].Tags["outcome"]).IsEqualTo("rejected");
    }

    [Test]
    public async Task RecordAuthRejection_IsASeparateInstrumentFromEnvelopes()
    {
        (IngestMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> envelopes = new(factory, VordMeter.Name, "vord.ingest.envelopes");
        using MetricCollector<long> rejections = new(factory, VordMeter.Name, "vord.ingest.auth_rejections");

        metrics.RecordAuthRejection(IngestAuthRejectionReason.UnknownKey);

        await Assert.That(envelopes.GetMeasurementSnapshot().Count).IsEqualTo(0);

        IReadOnlyList<CollectedMeasurement<long>> measurements = rejections.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["reason"]).IsEqualTo("unknown_key");
    }

    [Test]
    public async Task RecordStreamRejection_CountsTelemetryThatNeverBecameAnEnvelope()
    {
        (IngestMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> envelopes = new(factory, VordMeter.Name, "vord.ingest.envelopes");
        using MetricCollector<long> rejections = new(factory, VordMeter.Name, "vord.ingest.stream_rejections");

        metrics.RecordStreamRejection(IngestStreamRejectionReason.StreamLimit);

        await Assert.That(envelopes.GetMeasurementSnapshot().Count).IsEqualTo(0);

        IReadOnlyList<CollectedMeasurement<long>> measurements = rejections.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["reason"]).IsEqualTo("stream_limit");
    }

    [Test]
    public async Task RecordClockSkew_RecordsSecondsOnTheSkewHistogram()
    {
        (IngestMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<double> collector = new(factory, VordMeter.Name, "vord.ingest.clock_skew_seconds");

        metrics.RecordClockSkew(12.5);

        IReadOnlyList<CollectedMeasurement<double>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(12.5);
    }

    [Test]
    public async Task InitialiseSeries_EmitsAZeroForEveryClosedEnumValue()
    {
        // A counter exports no series until its first measurement, and this Prometheus does not
        // ingest created timestamps — so a series born mid-window shows no increase() and the very
        // first failure is invisible to every rule. Pre-recording zero is what buys that back.
        (IngestMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> envelopes = new(factory, VordMeter.Name, "vord.ingest.envelopes");
        using MetricCollector<long> auth = new(factory, VordMeter.Name, "vord.ingest.auth_rejections");
        using MetricCollector<long> streams = new(factory, VordMeter.Name, "vord.ingest.stream_rejections");

        metrics.InitialiseSeries();

        IReadOnlyList<CollectedMeasurement<long>> envelopeSeries = envelopes.GetMeasurementSnapshot();
        await Assert.That(envelopeSeries.Count).IsEqualTo(4);
        await Assert.That(envelopeSeries.All(measurement => measurement.Value == 0L)).IsTrue();
        await Assert.That(envelopeSeries.Select(measurement => (string?)measurement.Tags["outcome"]).ToList())
            .Contains("unavailable");
        await Assert.That(auth.GetMeasurementSnapshot().Count).IsEqualTo(2);
        await Assert.That(streams.GetMeasurementSnapshot().Count).IsEqualTo(3);
    }

    [Test]
    public async Task Instruments_CarryNoTenantTag()
    {
        (IngestMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.ingest.envelopes");

        metrics.RecordEnvelope(IngestOutcome.Rejected);

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements[0].Tags.ContainsKey("tenant")).IsFalse();
    }

    [Test]
    public async Task Constructor_NullMeterFactory_Throws()
    {
        await Assert.That(() => new IngestMetrics(null!)).Throws<ArgumentNullException>();
    }
}
