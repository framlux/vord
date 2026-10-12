// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Counts telemetry rows the Kubernetes inventory projection skipped instead of applying.
/// </summary>
/// <remarks>
/// A skipped row is never revisited, so the counter is the only evidence that a machine's report did
/// not reach the inventory. The reason tag is a closed enum. There is no tenant tag: the failures that
/// matter here are the projector's own, which affect every tenant at once, so a per-tenant series would
/// add cardinality without telling an operator anything the fleet-wide one does not.
/// </remarks>
public sealed class KubernetesInventoryMetrics : IInitialisableMetrics
{
    private readonly Counter<long> _projectionFailures;

    /// <summary>
    /// Creates the Kubernetes inventory instrument.
    /// </summary>
    /// <param name="meterFactory">The factory the SDK also observes.</param>
    public KubernetesInventoryMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        Meter meter = meterFactory.Create(VordMeter.Name);

        _projectionFailures = meter.CreateCounter<long>(
            "vord.kubernetes.projection_failures",
            description: "Kubernetes inventory rows the projector skipped, by reason.");
    }

    /// <summary>
    /// Records one telemetry row the projector skipped.
    /// </summary>
    /// <param name="reason">Why the row was skipped.</param>
    public void RecordProjectionFailure(KubernetesProjectionFailureReason reason)
    {
        _projectionFailures.Add(1, new KeyValuePair<string, object?>("reason", MetricTag.From(reason)));
    }

    /// <inheritdoc />
    public void InitialiseSeries()
    {
        foreach (KubernetesProjectionFailureReason reason in Enum.GetValues<KubernetesProjectionFailureReason>())
        {
            _projectionFailures.Add(0, new KeyValuePair<string, object?>("reason", MetricTag.From(reason)));
        }
    }
}
