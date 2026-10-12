// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Counts what goes wrong around the Kubernetes inventory: telemetry rows the projection skipped
/// instead of applying, and machine capability reads the cluster page could not complete.
/// </summary>
/// <remarks>
/// A skipped row is never revisited, so its counter is the only evidence that a machine's report did
/// not reach the inventory. The reason tag is a closed enum. There is no tenant tag on either series:
/// the failures that matter here are the projector's own and the capability store's, which affect every
/// tenant at once, so a per-tenant series would add cardinality without telling an operator anything
/// the fleet-wide one does not.
/// </remarks>
public sealed class KubernetesInventoryMetrics : IInitialisableMetrics
{
    private readonly Counter<long> _projectionFailures;
    private readonly Counter<long> _capabilityReadFailures;

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
        _capabilityReadFailures = meter.CreateCounter<long>(
            "vord.kubernetes.capability_read_failures",
            description: "Cluster page requests whose machine capabilities could not be read, so remote commands were reported as unknown.");
    }

    /// <summary>
    /// Records one telemetry row the projector skipped.
    /// </summary>
    /// <param name="reason">Why the row was skipped.</param>
    public void RecordProjectionFailure(KubernetesProjectionFailureReason reason)
    {
        _projectionFailures.Add(1, new KeyValuePair<string, object?>("reason", MetricTag.From(reason)));
    }

    /// <summary>
    /// Records one cluster page request that could not read its machines' capabilities.
    /// </summary>
    public void RecordCapabilityReadFailure()
    {
        _capabilityReadFailures.Add(1);
    }

    /// <inheritdoc />
    public void InitialiseSeries()
    {
        _capabilityReadFailures.Add(0);

        foreach (KubernetesProjectionFailureReason reason in Enum.GetValues<KubernetesProjectionFailureReason>())
        {
            _projectionFailures.Add(0, new KeyValuePair<string, object?>("reason", MetricTag.From(reason)));
        }
    }
}
