// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Pins the webhook-delivery series. The outcomes are kept apart because a receiver being briefly
/// down and a receiver permanently refusing our payload call for different responses, and only one
/// of them resolves on its own; the provider dimension is what separates "Slack is down" from "this
/// customer's URL is wrong".
/// </summary>
public sealed class IntegrationMetricsTests
{
    private static (IntegrationMetrics Metrics, IMeterFactory Factory) Build()
    {
        ServiceCollection services = new();
        services.AddMetrics();
        ServiceProvider provider = services.BuildServiceProvider();
        IMeterFactory factory = provider.GetRequiredService<IMeterFactory>();

        return (new IntegrationMetrics(factory), factory);
    }

    [Test]
    public async Task RecordDelivery_Delivered_RecordsFleetWideOnly()
    {
        (IntegrationMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> deliveries = new(factory, VordMeter.Name, "vord.integrations.deliveries");
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.integrations.delivery_failures");

        metrics.RecordDelivery(IntegrationDeliveryOutcome.Delivered, IntegrationProvider.Slack, tenantId: 7);

        IReadOnlyList<CollectedMeasurement<long>> measurements = deliveries.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["outcome"]).IsEqualTo("delivered");
        await Assert.That(measurements[0].Tags["provider"]).IsEqualTo("slack");
        await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(IntegrationDeliveryOutcome.Transient, "transient")]
    [Arguments(IntegrationDeliveryOutcome.Rejected, "rejected")]
    [Arguments(IntegrationDeliveryOutcome.Error, "error")]
    [Arguments(IntegrationDeliveryOutcome.Unformattable, "unformattable")]
    public async Task RecordDelivery_EveryFailure_RecordsBothCounters(
        IntegrationDeliveryOutcome outcome, string expected)
    {
        (IntegrationMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> deliveries = new(factory, VordMeter.Name, "vord.integrations.deliveries");
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.integrations.delivery_failures");

        metrics.RecordDelivery(outcome, IntegrationProvider.MicrosoftTeams, tenantId: 7);

        IReadOnlyList<CollectedMeasurement<long>> fleet = deliveries.GetMeasurementSnapshot();
        await Assert.That(fleet[0].Tags["outcome"]).IsEqualTo(expected);
        await Assert.That(fleet[0].Tags["provider"]).IsEqualTo("microsoft_teams");
        await Assert.That(fleet[0].Tags.ContainsKey("tenant")).IsFalse();

        IReadOnlyList<CollectedMeasurement<long>> attributed = failures.GetMeasurementSnapshot();
        await Assert.That(attributed.Count).IsEqualTo(1);
        await Assert.That(attributed[0].Tags["tenant"]).IsEqualTo("7");
        await Assert.That(attributed[0].Tags["outcome"]).IsEqualTo(expected);
    }

    [Test]
    public async Task RecordDelivery_FailureWithNoTenantInScope_AttributesToTheUnknownBucket()
    {
        (IntegrationMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.integrations.delivery_failures");

        metrics.RecordDelivery(IntegrationDeliveryOutcome.Error, IntegrationProvider.Discord, tenantId: null);

        IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
        await Assert.That(measurements[0].Tags["tenant"]).IsEqualTo("unknown");
    }

    [Test]
    public async Task InitialiseSeries_CreatesEveryOutcomeAndProviderCombination()
    {
        (IntegrationMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> deliveries = new(factory, VordMeter.Name, "vord.integrations.deliveries");
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.integrations.delivery_failures");

        metrics.InitialiseSeries();

        // Five outcomes by six providers.
        IReadOnlyList<CollectedMeasurement<long>> measurements = deliveries.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(30);
        await Assert.That(measurements.All(measurement => measurement.Value == 0L)).IsTrue();
        await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }
}
