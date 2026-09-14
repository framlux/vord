// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Pins the registration series. Onboarding failing silently is the worst first impression the
/// product can make, and the failures that matter most are the ones with no tenant in scope —
/// which is why the unknown bucket is part of the contract rather than a fallback.
/// </summary>
public sealed class RegistrationMetricsTests
{
    private static (RegistrationMetrics Metrics, IMeterFactory Factory) Build()
    {
        ServiceCollection services = new();
        services.AddMetrics();
        ServiceProvider provider = services.BuildServiceProvider();
        IMeterFactory factory = provider.GetRequiredService<IMeterFactory>();

        return (new RegistrationMetrics(factory), factory);
    }

    [Test]
    public async Task RecordAttempt_Registered_RecordsFleetWideOnly()
    {
        (RegistrationMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> attempts = new(factory, VordMeter.Name, "vord.registration.attempts");
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.registration.failures");

        metrics.RecordAttempt(RegistrationOutcome.Registered, tenantId: 3);

        await Assert.That(attempts.GetMeasurementSnapshot()[0].Tags["outcome"]).IsEqualTo("registered");
        await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(RegistrationOutcome.MissingToken, "missing_token")]
    [Arguments(RegistrationOutcome.InvalidToken, "invalid_token")]
    [Arguments(RegistrationOutcome.RevokedToken, "revoked_token")]
    [Arguments(RegistrationOutcome.ExpiredToken, "expired_token")]
    [Arguments(RegistrationOutcome.ConsumedToken, "consumed_token")]
    [Arguments(RegistrationOutcome.DuplicateMachine, "duplicate_machine")]
    [Arguments(RegistrationOutcome.MachineLimitExceeded, "machine_limit_exceeded")]
    [Arguments(RegistrationOutcome.Error, "error")]
    public async Task RecordAttempt_EveryFailure_IsSnakeCasedAndAttributed(
        RegistrationOutcome outcome, string expected)
    {
        (RegistrationMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> attempts = new(factory, VordMeter.Name, "vord.registration.attempts");
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.registration.failures");

        metrics.RecordAttempt(outcome, tenantId: 3);

        await Assert.That(attempts.GetMeasurementSnapshot()[0].Tags["outcome"]).IsEqualTo(expected);

        IReadOnlyList<CollectedMeasurement<long>> attributed = failures.GetMeasurementSnapshot();
        await Assert.That(attributed.Count).IsEqualTo(1);
        await Assert.That(attributed[0].Tags["tenant"]).IsEqualTo("3");
        await Assert.That(attributed[0].Tags["outcome"]).IsEqualTo(expected);
    }

    [Test]
    public async Task RecordAttempt_FailureBeforeTheTokenResolves_UsesTheUnknownBucket()
    {
        // A bad token is how a tenant fails to be identified at all, so the failure that matters
        // most is the one with nothing to attribute it to.
        (RegistrationMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.registration.failures");

        metrics.RecordAttempt(RegistrationOutcome.InvalidToken, tenantId: null);

        await Assert.That(failures.GetMeasurementSnapshot()[0].Tags["tenant"]).IsEqualTo("unknown");
    }

    [Test]
    public async Task InitialiseSeries_CreatesEveryFleetWideOutcomeAndTheUnknownTenantFailures()
    {
        (RegistrationMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> attempts = new(factory, VordMeter.Name, "vord.registration.attempts");
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.registration.failures");

        metrics.InitialiseSeries();

        IReadOnlyList<CollectedMeasurement<long>> measurements = attempts.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(9);
        await Assert.That(measurements.All(measurement => measurement.Value == 0L)).IsTrue();

        // The eight failing outcomes in the unknown tenant bucket. A bad token resolves no tenant,
        // so this is the series the onboarding alert reads, and it has to exist before the first
        // failure rather than be born by it.
        IReadOnlyList<CollectedMeasurement<long>> attributed = failures.GetMeasurementSnapshot();
        await Assert.That(attributed.Count).IsEqualTo(8);
        await Assert.That(attributed.All(measurement => measurement.Value == 0L)).IsTrue();
        await Assert.That(attributed.All(measurement => "unknown".Equals(measurement.Tags["tenant"]))).IsTrue();
        await Assert.That(attributed.Any(measurement => "registered".Equals(measurement.Tags["outcome"]))).IsFalse();
    }
}
