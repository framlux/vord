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
/// Pins the login series, including the rule that keeps per-tenant SSO from putting a customer's
/// provider name into the label set, and the attribution that answers whose identity provider
/// broke without anybody reading logs.
/// </summary>
public sealed class AuthMetricsTests
{
    private static (AuthMetrics Metrics, IMeterFactory Factory) Build()
    {
        ServiceCollection services = new();
        services.AddMetrics();
        ServiceProvider provider = services.BuildServiceProvider();
        IMeterFactory factory = provider.GetRequiredService<IMeterFactory>();

        return (new AuthMetrics(factory), factory);
    }

    [Test]
    [Arguments(AuthProviderType.GitHub, "git_hub")]
    [Arguments(AuthProviderType.Google, "google")]
    [Arguments(AuthProviderType.Microsoft, "microsoft")]
    [Arguments(AuthProviderType.CustomOidc, "custom_oidc")]
    public async Task RecordLogin_TagsTheProvider(AuthProviderType provider, string expected)
    {
        (AuthMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.auth.logins");

        metrics.RecordLogin(provider, LoginOutcome.Succeeded);

        await Assert.That(collector.GetMeasurementSnapshot()[0].Tags["provider"]).IsEqualTo(expected);
    }

    [Test]
    [Arguments(LoginOutcome.Succeeded, "succeeded")]
    [Arguments(LoginOutcome.Rejected, "rejected")]
    [Arguments(LoginOutcome.Failed, "failed")]
    public async Task RecordLogin_TagsTheOutcome(LoginOutcome outcome, string expected)
    {
        (AuthMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.auth.logins");

        metrics.RecordLogin(AuthProviderType.Google, outcome);

        await Assert.That(collector.GetMeasurementSnapshot()[0].Tags["outcome"]).IsEqualTo(expected);
    }

    [Test]
    public async Task RecordLogin_CustomOidcIsOneValueForEveryTenant()
    {
        // A per-tenant provider slug would be a caller-influenced tag value growing with the
        // customer count, which is what the closed-enum rule exists to prevent.
        (AuthMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.auth.logins");

        metrics.RecordLogin(AuthProviderType.CustomOidc, LoginOutcome.Succeeded);
        metrics.RecordLogin(AuthProviderType.CustomOidc, LoginOutcome.Succeeded);

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements
            .Select(measurement => (string?)measurement.Tags["provider"])
            .Distinct()
            .Count()).IsEqualTo(1);
    }

    [Test]
    public async Task RecordSsoFailure_AttributesToTheTenantWhoseProviderBroke()
    {
        (AuthMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.auth.sso_login_failures");

        metrics.RecordSsoFailure(LoginOutcome.Failed, tenantId: 42);

        IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["tenant"]).IsEqualTo("42");
        await Assert.That(measurements[0].Tags["outcome"]).IsEqualTo("failed");
    }

    [Test]
    public async Task RecordSsoFailure_AlsoMovesTheFleetWideCounterWithoutATenantTag()
    {
        // One call records both, so a call site cannot record attribution without alerting or the
        // other way round — which is how the two would drift out of step.
        (AuthMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> logins = new(factory, VordMeter.Name, "vord.auth.logins");

        metrics.RecordSsoFailure(LoginOutcome.Failed, tenantId: 42);

        IReadOnlyList<CollectedMeasurement<long>> measurements = logins.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["provider"]).IsEqualTo("custom_oidc");
        await Assert.That(measurements[0].Tags.ContainsKey("tenant")).IsFalse();
    }

    [Test]
    public async Task RecordSsoFailure_WithNoTenantResolved_UsesTheUnknownBucket()
    {
        // One of the nine SSO failure sites fires before the tenant id is read from the
        // authentication properties, so the bucket has to exist.
        (AuthMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.auth.sso_login_failures");

        metrics.RecordSsoFailure(LoginOutcome.Failed, tenantId: null);

        await Assert.That(failures.GetMeasurementSnapshot()[0].Tags["tenant"]).IsEqualTo("unknown");
    }

    [Test]
    public async Task InitialiseSeries_CreatesEveryProviderAndOutcomeCombination()
    {
        (AuthMetrics metrics, IMeterFactory factory) = Build();
        using MetricCollector<long> logins = new(factory, VordMeter.Name, "vord.auth.logins");
        using MetricCollector<long> failures = new(factory, VordMeter.Name, "vord.auth.sso_login_failures");

        metrics.InitialiseSeries();

        // Five providers, including Unknown, by three outcomes.
        IReadOnlyList<CollectedMeasurement<long>> measurements = logins.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(15);
        await Assert.That(measurements.All(measurement => measurement.Value == 0L)).IsTrue();
        await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }
}
