// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Counts alert deliveries to tenant-configured integration endpoints.
/// </summary>
/// <remarks>
/// A customer whose webhooks silently stopped arriving has no way to tell us, so this answers a
/// question on their behalf. The provider dimension is on the fleet-wide counter because "is Slack
/// down for everybody" and "is this one endpoint misconfigured" are different incidents with
/// different responses.
/// </remarks>
public sealed class IntegrationMetrics : IInitialisableMetrics
{
    private readonly Counter<long> _deliveries;
    private readonly Counter<long> _deliveryFailures;

    /// <summary>
    /// Creates the integration instruments.
    /// </summary>
    /// <param name="meterFactory">The factory the SDK also observes.</param>
    public IntegrationMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        Meter meter = meterFactory.Create(VordMeter.Name);

        _deliveries = meter.CreateCounter<long>(
            "vord.integrations.deliveries",
            description: "Alert deliveries to tenant integration endpoints, by outcome and provider.");

        _deliveryFailures = meter.CreateCounter<long>(
            "vord.integrations.delivery_failures",
            description: "Integration delivery failures attributed to the tenant that owns the endpoint.");
    }

    /// <summary>
    /// Records one delivery attempt to one endpoint.
    /// </summary>
    /// <param name="outcome">How the attempt ended.</param>
    /// <param name="provider">The endpoint's provider.</param>
    /// <param name="tenantId">The internal tenant id, or null where none is in scope. Used only on
    /// failures.</param>
    public void RecordDelivery(IntegrationDeliveryOutcome outcome, IntegrationProvider provider, int? tenantId)
    {
        KeyValuePair<string, object?> outcomeTag = new("outcome", MetricTag.From(outcome));
        KeyValuePair<string, object?> providerTag = new("provider", MetricTag.From(provider));

        _deliveries.Add(1, outcomeTag, providerTag);

        if (outcome == IntegrationDeliveryOutcome.Delivered)
        {
            return;
        }

        string tenant = tenantId is null
            ? MetricTag.Unknown
            : tenantId.Value.ToString(CultureInfo.InvariantCulture);

        _deliveryFailures.Add(
            1,
            new KeyValuePair<string, object?>("tenant", tenant),
            outcomeTag,
            providerTag);
    }

    /// <inheritdoc />
    public void InitialiseSeries()
    {
        foreach (IntegrationDeliveryOutcome outcome in Enum.GetValues<IntegrationDeliveryOutcome>())
        {
            foreach (IntegrationProvider provider in Enum.GetValues<IntegrationProvider>())
            {
                _deliveries.Add(
                    0,
                    new KeyValuePair<string, object?>("outcome", MetricTag.From(outcome)),
                    new KeyValuePair<string, object?>("provider", MetricTag.From(provider)));
            }
        }
    }
}
