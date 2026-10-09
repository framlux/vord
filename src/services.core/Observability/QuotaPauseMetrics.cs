// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Counts quota pause decisions that could not be made.
/// </summary>
/// <remarks>
/// Every surface treats an unavailable decision as "not paused" so that an outage never stops a
/// paying fleet reporting. That makes this counter the only evidence that limits stopped being
/// enforced, and the surface tag says which path is running unmetered. The tag is a closed enum and
/// there is no tenant tag: an outage affects every tenant at once, so a per-tenant series would add
/// cardinality without telling an operator anything the fleet-wide one does not.
/// </remarks>
public sealed class QuotaPauseMetrics : IInitialisableMetrics
{
    private readonly Counter<long> _checkFailures;

    /// <summary>
    /// Creates the quota pause instrument.
    /// </summary>
    /// <param name="meterFactory">The factory the SDK also observes.</param>
    public QuotaPauseMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        Meter meter = meterFactory.Create(VordMeter.Name);

        _checkFailures = meter.CreateCounter<long>(
            "vord.quota.pause_check_failures",
            description: "Quota pause decisions that could not be made, by the surface that asked.");
    }

    /// <summary>
    /// Records one pause decision that could not be made.
    /// </summary>
    /// <param name="surface">The surface that asked.</param>
    public void RecordCheckFailure(QuotaCheckSurface surface)
    {
        _checkFailures.Add(1, new KeyValuePair<string, object?>("surface", MetricTag.From(surface)));
    }

    /// <inheritdoc />
    public void InitialiseSeries()
    {
        foreach (QuotaCheckSurface surface in Enum.GetValues<QuotaCheckSurface>())
        {
            _checkFailures.Add(0, new KeyValuePair<string, object?>("surface", MetricTag.From(surface)));
        }
    }
}
