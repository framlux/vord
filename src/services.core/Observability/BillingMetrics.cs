// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Counts calls from the fleet to the billing control plane.
/// </summary>
/// <remarks>
/// Recorded inside the real client rather than on its interface: the self-hosted deployment
/// substitutes a no-op client that returns unconditional successes, and instrumenting the interface
/// would record those as real billing operations.
/// </remarks>
public sealed class BillingMetrics : IInitialisableMetrics
{
    private readonly Counter<long> _operations;

    /// <summary>
    /// Creates the billing instrument.
    /// </summary>
    /// <param name="meterFactory">The factory the SDK also observes.</param>
    public BillingMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        Meter meter = meterFactory.Create(VordMeter.Name);

        _operations = meter.CreateCounter<long>(
            "vord.billing.operations",
            description: "Calls to the billing control plane, by operation and outcome.");
    }

    /// <summary>
    /// Records one control-plane call.
    /// </summary>
    /// <param name="operation">Which call was made.</param>
    /// <param name="outcome">How it ended.</param>
    public void RecordOperation(BillingOperation operation, BillingOperationOutcome outcome)
    {
        _operations.Add(
            1,
            new KeyValuePair<string, object?>("operation", MetricTag.From(operation)),
            new KeyValuePair<string, object?>("outcome", MetricTag.From(outcome)));
    }

    /// <inheritdoc />
    public void InitialiseSeries()
    {
        foreach (BillingOperation operation in Enum.GetValues<BillingOperation>())
        {
            foreach (BillingOperationOutcome outcome in Enum.GetValues<BillingOperationOutcome>())
            {
                _operations.Add(
                    0,
                    new KeyValuePair<string, object?>("operation", MetricTag.From(operation)),
                    new KeyValuePair<string, object?>("outcome", MetricTag.From(outcome)));
            }
        }
    }
}
