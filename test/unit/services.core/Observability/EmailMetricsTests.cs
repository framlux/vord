// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Notifications;
using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Pins the email series. Two rules make it useful: a skipped send is terminal success rather than
/// a failure, and attribution lives on its own counter so the series an alert rule reads is one
/// that exists before the incident rather than one born during it.
/// </summary>
public sealed class EmailMetricsTests
{
    private static (EmailMetrics Metrics, IMeterFactory Factory) Build()
    {
        ServiceCollection services = new();
        services.AddMetrics();
        ServiceProvider provider = services.BuildServiceProvider();
        IMeterFactory factory = provider.GetRequiredService<IMeterFactory>();

        return (new EmailMetrics(factory), factory);
    }

    [Test]
    public async Task RecordSend_Sent_RecordsFleetWideOnlyAndCarriesNoTenant()
    {
        (EmailMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> sends = new(factory, VordMeter.Name, "vord.email.sends");
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.email.send_failures");

        metrics.RecordSend(EmailDeliveryOutcome.Sent, EmailPurpose.Invitation, tenantId: 12);

        IReadOnlyList<CollectedMeasurement<long>> measurements = sends.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["outcome"]).IsEqualTo("sent");
        await Assert.That(measurements[0].Tags["purpose"]).IsEqualTo("invitation");
        await Assert.That(measurements[0].Tags.ContainsKey("tenant")).IsFalse();
        await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task RecordSend_Failed_RecordsBothCountersAndKeepsTheFleetSeriesUntenanted()
    {
        // The fleet-wide series is the one rules read, so it must move on a failure without
        // acquiring a tenant dimension — otherwise the pre-recorded zero and the real increment
        // are two different series and increase() never sees the first failure.
        (EmailMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> sends = new(factory, VordMeter.Name, "vord.email.sends");
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.email.send_failures");

        metrics.RecordSend(EmailDeliveryOutcome.Failed, EmailPurpose.Alert, tenantId: 12);

        IReadOnlyList<CollectedMeasurement<long>> fleet = sends.GetMeasurementSnapshot();
        await Assert.That(fleet.Count).IsEqualTo(1);
        await Assert.That(fleet[0].Tags["outcome"]).IsEqualTo("failed");
        await Assert.That(fleet[0].Tags.ContainsKey("tenant")).IsFalse();

        IReadOnlyList<CollectedMeasurement<long>> attributed = failures.GetMeasurementSnapshot();
        await Assert.That(attributed.Count).IsEqualTo(1);
        await Assert.That(attributed[0].Tags["tenant"]).IsEqualTo("12");
        await Assert.That(attributed[0].Tags["purpose"]).IsEqualTo("alert");
    }

    [Test]
    public async Task RecordSend_FailedWithNoTenantInScope_AttributesToTheUnknownBucket()
    {
        (EmailMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.email.send_failures");

        metrics.RecordSend(EmailDeliveryOutcome.Failed, EmailPurpose.Invitation, tenantId: null);

        IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
        await Assert.That(measurements[0].Tags["tenant"]).IsEqualTo("unknown");
    }

    [Test]
    public async Task RecordSend_Skipped_IsTerminalSuccessAndAttributesNothing()
    {
        // No provider configured is a supported deployment. A rule paging on non-sent would page
        // forever in every self-hosted install.
        (EmailMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> sends = new(factory, VordMeter.Name, "vord.email.sends");
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.email.send_failures");

        metrics.RecordSend(EmailDeliveryOutcome.Skipped, EmailPurpose.Alert, tenantId: 12);

        await Assert.That(sends.GetMeasurementSnapshot()[0].Tags["outcome"]).IsEqualTo("skipped");
        await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task RecordUndeliverable_CountsAnAlertThatWasNeverSent()
    {
        // A tenant with no admin recipients gets no alert and no send is attempted, so there is no
        // delivery outcome to record — and without this the customer's silence is invisible.
        (EmailMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> sends = new(factory, VordMeter.Name, "vord.email.sends");
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.email.send_failures");

        metrics.RecordUndeliverable(EmailPurpose.Alert, tenantId: 12);

        IReadOnlyList<CollectedMeasurement<long>> fleet = sends.GetMeasurementSnapshot();
        await Assert.That(fleet.Count).IsEqualTo(1);
        await Assert.That(fleet[0].Tags["outcome"]).IsEqualTo("undeliverable");
        await Assert.That(failures.GetMeasurementSnapshot()[0].Tags["tenant"]).IsEqualTo("12");
    }

    [Test]
    public async Task InitialiseSeries_CreatesEveryFleetWideCombinationAndNoAttributedOne()
    {
        // Attributed series are per-tenant and cannot be pre-created; that limit is why alerting
        // reads the fleet-wide counter and attribution is a drill-down.
        (EmailMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> sends = new(factory, VordMeter.Name, "vord.email.sends");
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.email.send_failures");

        metrics.InitialiseSeries();

        // Four outcomes — three delivery outcomes plus undeliverable — by two purposes.
        IReadOnlyList<CollectedMeasurement<long>> measurements = sends.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(8);
        await Assert.That(measurements.All(measurement => measurement.Value == 0L)).IsTrue();
        await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }
}
