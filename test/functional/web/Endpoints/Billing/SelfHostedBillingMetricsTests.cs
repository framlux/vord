// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.FunctionalTest.Endpoints.Billing;

/// <summary>
/// Guards the rule that the control-plane counter is recorded inside the real client and nowhere
/// else.
/// </summary>
/// <remarks>
/// A self-hosted deployment substitutes a no-op billing client that returns unconditional
/// successes. Instrumenting the interface — or a decorator over it — would record those as real
/// billing operations, telling an operator their billing integration is healthy in a deployment
/// that has no billing integration at all.
/// </remarks>
public sealed class SelfHostedBillingMetricsTests
{
    [Test]
    public async Task SelfHostedBillingCall_RecordsNothing()
    {
        using SelfHostedTestFactory factory = new();
        IMeterFactory meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using MetricCollector<long> operations = new(meterFactory, VordMeter.Name, "vord.billing.operations");

        IBillingApiClient client = factory.Services.GetRequiredService<IBillingApiClient>();
        await client.UpdateQuantityAsync("ext-tenant", 5, CancellationToken.None);

        await Assert.That(operations.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }
}
