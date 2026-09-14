// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Instruments covering telemetry ingest: how envelopes were answered, streams the server refused
/// before any envelope existed, agent requests refused at the key layer, and how far drifted agent
/// clocks are.
/// </summary>
/// <remarks>
/// Call sites pass values, never tag names. Keeping the tag vocabulary inside this class is what
/// makes the cardinality and naming rules enforceable in one place instead of at every recording
/// site.
/// </remarks>
public sealed class IngestMetrics : IInitialisableMetrics
{
    private readonly Counter<long> _envelopes;
    private readonly Counter<long> _streamRejections;
    private readonly Counter<long> _authRejections;
    private readonly Histogram<double> _clockSkewSeconds;

    /// <summary>
    /// Creates the ingest instruments.
    /// </summary>
    /// <param name="meterFactory">The factory the SDK also observes.</param>
    public IngestMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        Meter meter = meterFactory.Create(VordMeter.Name);

        _envelopes = meter.CreateCounter<long>(
            "vord.ingest.envelopes",
            description: "Telemetry envelopes answered by the telemetry service, by terminal outcome.");

        _streamRejections = meter.CreateCounter<long>(
            "vord.ingest.stream_rejections",
            description: "Telemetry streams refused at open, or closed by the server mid-stream.");

        _authRejections = meter.CreateCounter<long>(
            "vord.ingest.auth_rejections",
            description: "Agent requests refused by API key authentication before reaching a service.");

        _clockSkewSeconds = meter.CreateHistogram<double>(
            "vord.ingest.clock_skew_seconds",
            unit: "s",
            description: "Agent clock skew magnitude for envelopes exceeding the skew threshold.");
    }

    /// <summary>
    /// Records how one envelope was answered.
    /// </summary>
    /// <param name="outcome">The envelope's terminal state.</param>
    public void RecordEnvelope(IngestOutcome outcome)
    {
        _envelopes.Add(1, new KeyValuePair<string, object?>("outcome", MetricTag.From(outcome)));
    }

    /// <summary>
    /// Records one telemetry stream the server refused to open, or closed itself.
    /// </summary>
    /// <param name="reason">Why the stream was refused or closed.</param>
    public void RecordStreamRejection(IngestStreamRejectionReason reason)
    {
        _streamRejections.Add(1, new KeyValuePair<string, object?>("reason", MetricTag.From(reason)));
    }

    /// <summary>
    /// Records one agent request refused at the authentication layer.
    /// </summary>
    /// <param name="reason">Why it was refused.</param>
    public void RecordAuthRejection(IngestAuthRejectionReason reason)
    {
        _authRejections.Add(1, new KeyValuePair<string, object?>("reason", MetricTag.From(reason)));
    }

    /// <summary>
    /// Records the magnitude of an agent clock skew that exceeded the accepted threshold. This is
    /// deliberately a conditional measurement, not a distribution over all envelopes — nothing may
    /// read it as general ingest timing.
    /// </summary>
    /// <param name="seconds">Skew magnitude in seconds.</param>
    public void RecordClockSkew(double seconds)
    {
        _clockSkewSeconds.Record(seconds);
    }

    /// <inheritdoc />
    public void InitialiseSeries()
    {
        foreach (IngestOutcome outcome in Enum.GetValues<IngestOutcome>())
        {
            _envelopes.Add(0, new KeyValuePair<string, object?>("outcome", MetricTag.From(outcome)));
        }

        foreach (IngestStreamRejectionReason reason in Enum.GetValues<IngestStreamRejectionReason>())
        {
            _streamRejections.Add(0, new KeyValuePair<string, object?>("reason", MetricTag.From(reason)));
        }

        foreach (IngestAuthRejectionReason reason in Enum.GetValues<IngestAuthRejectionReason>())
        {
            _authRejections.Add(0, new KeyValuePair<string, object?>("reason", MetricTag.From(reason)));
        }
    }
}
