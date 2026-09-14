// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Counts alert events as they fire, by the metric the rule watches and its severity.
/// </summary>
/// <remarks>
/// Deliberately not a funnel. Evaluation has several early-skip paths, a met condition is not a
/// firing because of duration windows and active-event deduplication, and delivery multiplies per
/// integration and per retry — counts at those stages would not compose into anything an operator
/// could reason about.
///
/// Equally deliberately not a liveness signal: an idle installation fires nothing, so silence here
/// is the healthy state and cannot be alerted on. Whether the evaluator is running and whether it
/// is throwing are answered by the job instruments instead. What this gives is the shape of an
/// incident while it is happening, and the record of it afterwards.
///
/// A re-enqueued delivery for an already-fired event is not a firing and must not be recorded.
/// </remarks>
public sealed class AlertPipelineMetrics : IInitialisableMetrics
{
    private readonly Counter<long> _events;

    /// <summary>
    /// Creates the alert instrument.
    /// </summary>
    /// <param name="meterFactory">The factory the SDK also observes.</param>
    public AlertPipelineMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        Meter meter = meterFactory.Create(VordMeter.Name);

        _events = meter.CreateCounter<long>(
            "vord.alerts.events",
            description: "Alert events fired, by rule metric and severity.");
    }

    /// <summary>
    /// Records one alert event firing.
    /// </summary>
    /// <param name="metric">The metric the rule watches.</param>
    /// <param name="severity">The rule's severity.</param>
    public void RecordEventFired(AlertMetric metric, AlertSeverity severity)
    {
        _events.Add(
            1,
            new KeyValuePair<string, object?>("metric", MetricTag.From(metric)),
            new KeyValuePair<string, object?>("severity", MetricTag.From(severity)));
    }

    /// <inheritdoc />
    public void InitialiseSeries()
    {
        foreach (AlertMetric metric in Enum.GetValues<AlertMetric>())
        {
            foreach (AlertSeverity severity in Enum.GetValues<AlertSeverity>())
            {
                _events.Add(
                    0,
                    new KeyValuePair<string, object?>("metric", MetricTag.From(metric)),
                    new KeyValuePair<string, object?>("severity", MetricTag.From(severity)));
            }
        }
    }
}
