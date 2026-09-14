// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Pins the control-plane series. The distinction that earns this instrument its place is that a
/// refusal and an unreachable service are different outcomes: the calls answer with a failure flag
/// rather than throwing, so a catch-only instrument would report perfect health while every billing
/// operation failed.
/// </summary>
public sealed class BillingMetricsTests
{
    private static (BillingMetrics Metrics, IMeterFactory Factory) Build()
    {
        ServiceCollection services = new();
        services.AddMetrics();
        ServiceProvider provider = services.BuildServiceProvider();
        IMeterFactory factory = provider.GetRequiredService<IMeterFactory>();

        return (new BillingMetrics(factory), factory);
    }

    [Test]
    public async Task RecordOperation_TagsOperationAndOutcome()
    {
        (BillingMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.billing.operations");

        metrics.RecordOperation(BillingOperation.SwapSubscriptionPrice, BillingOperationOutcome.Ok);

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["operation"]).IsEqualTo("swap_subscription_price");
        await Assert.That(measurements[0].Tags["outcome"]).IsEqualTo("ok");
    }

    [Test]
    [Arguments(BillingOperationOutcome.Failed, "failed")]
    [Arguments(BillingOperationOutcome.Error, "error")]
    public async Task RecordOperation_RefusalAndUnreachableAreDistinct(
        BillingOperationOutcome outcome, string expected)
    {
        (BillingMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.billing.operations");

        metrics.RecordOperation(BillingOperation.UpdateQuantity, outcome);

        await Assert.That(collector.GetMeasurementSnapshot()[0].Tags["outcome"]).IsEqualTo(expected);
    }

    [Test]
    public async Task RecordOperation_CarriesNoTenantTag()
    {
        // Only the external identifier is in scope at the recording site, and a second identifier
        // space in the label set costs the same cardinality as the one the allowlist rejects.
        (BillingMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.billing.operations");

        metrics.RecordOperation(BillingOperation.DeleteCustomer, BillingOperationOutcome.Failed);

        await Assert.That(collector.GetMeasurementSnapshot()[0].Tags.ContainsKey("tenant")).IsFalse();
    }

    [Test]
    public async Task InitialiseSeries_CreatesEveryOperationAndOutcomeCombination()
    {
        (BillingMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.billing.operations");

        metrics.InitialiseSeries();

        // Ten operations by three outcomes. The read-only methods have no refusal flag, so their
        // failed series stays at zero — pre-creating it is a convenience, not a claim it is
        // reachable.
        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(30);
        await Assert.That(measurements.All(measurement => measurement.Value == 0L)).IsTrue();
    }
}
