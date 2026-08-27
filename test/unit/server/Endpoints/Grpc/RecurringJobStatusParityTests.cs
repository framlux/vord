// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Hangfire;
using Framlux.Vord.BillingGrpc;

namespace Framlux.FleetManagement.Test.Server.Endpoints.Grpc;

/// <summary>
/// Locks the fleet-side health enum to the wire enum. The mapping has a catch-all arm, so a new
/// health value added without a matching contract value would map silently to UNSPECIFIED — which
/// the panel is required to render as unknown, meaning a genuinely unhealthy job would show as
/// neither healthy nor broken. This test fails at the point the value is added instead.
/// </summary>
public sealed class RecurringJobStatusParityTests
{
    [Test]
    public async Task EveryHealthValue_HasAMatchingContractStatus()
    {
        List<string> missing = new();

        foreach (RecurringJobHealth health in Enum.GetValues<RecurringJobHealth>())
        {
            if (health == RecurringJobHealth.Unknown)
            {
                continue;
            }

            if (Enum.TryParse(typeof(FleetRecurringJobStatus), health.ToString(), ignoreCase: true, out object? _) == false)
            {
                missing.Add(health.ToString());
            }
        }

        await Assert.That(missing).IsEmpty();
    }
}
