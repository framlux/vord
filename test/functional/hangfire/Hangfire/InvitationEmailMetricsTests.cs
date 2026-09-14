// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Notifications;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.FunctionalTest.Hangfire;

/// <summary>
/// Proves the invitation job records its delivery outcome on the real host, not merely that the
/// metric class works in isolation.
/// </summary>
/// <remarks>
/// The case that matters is <c>skipped</c>. A deployment with no email provider is supported, and
/// its sends must never look like an outage — a rule paging on anything other than <c>sent</c>
/// would page forever in every self-hosted install. The test factory registers
/// <c>NoOpEmailService</c>, so that outcome arises naturally with no substitution.
/// </remarks>
public sealed class InvitationEmailMetricsTests
{
    [Test]
    public async Task InvitationEmailWithNoProviderConfigured_RecordsSkippedAndNotFailed()
    {
        using FunctionalTestFactory factory = new();

        // Resolving from the factory starts the host, so the startup zero-series pass has already
        // happened before the collectors below begin observing. Do not reorder these two lines.
        IMeterFactory meterFactory = factory.Services.GetRequiredService<IMeterFactory>();
        using MetricCollector<long> sends = new(meterFactory, VordMeter.Name, "vord.email.sends");
        using MetricCollector<long> failures = new(meterFactory, VordMeter.Name, "vord.email.send_failures");

        // The job is registered scoped, so it cannot be resolved from the root provider.
        using IServiceScope scope = factory.Services.CreateScope();
        SendInvitationEmailJob job = scope.ServiceProvider.GetRequiredService<SendInvitationEmailJob>();

        await job.SendAsync(
            "someone@test.invalid",
            12,
            "Test Tenant",
            "Inviter",
            "https://test.invalid/accept",
            CancellationToken.None);

        IReadOnlyList<CollectedMeasurement<long>> measurements = sends.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["outcome"]).IsEqualTo("skipped");
        await Assert.That(measurements[0].Tags["purpose"]).IsEqualTo("invitation");

        // Skipped is terminal success, so nothing is attributed to the tenant.
        await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }
}
