// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Counts work admitted without its Redis-backed protection because Redis was unavailable.
/// </summary>
/// <remarks>
/// A rising value means a protection is degraded, and it counts requests actually affected rather
/// than a connection state. That is why no separate Redis up/down gauge exists: a gauge would say
/// Redis is down while this says how much it cost.
/// </remarks>
public sealed class ResilienceMetrics : IInitialisableMetrics
{
    private readonly Counter<long> _failOpen;

    /// <summary>
    /// Creates the resilience instrument.
    /// </summary>
    /// <param name="meterFactory">The factory the SDK also observes.</param>
    public ResilienceMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        Meter meter = meterFactory.Create(VordMeter.Name);

        _failOpen = meter.CreateCounter<long>(
            "vord.redis.fail_open",
            description: "Work admitted without its Redis-backed protection because Redis was unavailable.");
    }

    /// <summary>
    /// Records one fail-open admission.
    /// </summary>
    /// <param name="component">Which protection was unavailable.</param>
    public void RecordFailOpen(ResilienceComponent component)
    {
        _failOpen.Add(1, new KeyValuePair<string, object?>("component", MetricTag.From(component)));
    }

    /// <inheritdoc />
    public void InitialiseSeries()
    {
        foreach (ResilienceComponent component in Enum.GetValues<ResilienceComponent>())
        {
            _failOpen.Add(0, new KeyValuePair<string, object?>("component", MetricTag.From(component)));
        }
    }
}
