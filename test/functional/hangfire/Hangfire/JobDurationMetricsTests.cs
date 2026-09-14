// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Test.Infrastructure;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.FunctionalTest.Hangfire;

/// <summary>
/// Proves the duration filter is invoked by a real Hangfire processing server, and that it records
/// exactly once per run.
/// </summary>
/// <remarks>
/// The exact-count assertion is the point. The filter is registered through
/// <c>IGlobalConfiguration.UseFilter</c>, which mutates Hangfire's process-global filter collection,
/// and this project builds several hosts in one process. If a per-host registration accumulated,
/// one job run would record two or three measurements — so an exact count is what catches it, and
/// relaxing it to "at least one" would hide precisely the defect worth knowing about.
/// </remarks>
[NotInParallel]
public sealed class JobDurationMetricsTests
{
    [Test]
    public async Task RunningOneJob_RecordsExactlyOneDurationMeasurement()
    {
        using FunctionalTestFactory factory = new();
        SmokeSink sink = new();

        factory.EnableHangfireProcessingServer = true;
        factory.AdditionalTestServices = services =>
        {
            services.AddSingleton(sink);
            services.AddScoped<SmokeJob>();
        };

        // Touching Services forces the host to build with the overrides above applied.
        IBackgroundJobClient client = factory.Services.GetRequiredService<IBackgroundJobClient>();
        IMeterFactory meterFactory = factory.Services.GetRequiredService<IMeterFactory>();

        using MetricCollector<double> durations = new(
            meterFactory, VordMeter.Name, "vord.jobs.duration_seconds");

        client.Enqueue<SmokeJob>(j => j.RunAsync(CancellationToken.None));

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while ((sink.RanAt is null) && (DateTimeOffset.UtcNow < deadline))
        {
            await Task.Delay(50);
        }

        await Assert.That(sink.RanAt).IsNotNull();


        // The filter records after the job body returns, so allow the server a moment to finish.
        DateTimeOffset metricDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while ((durations.GetMeasurementSnapshot().Count == 0) && (DateTimeOffset.UtcNow < metricDeadline))
        {
            await Task.Delay(50);
        }

        IReadOnlyList<CollectedMeasurement<double>> measurements = durations.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Tags["outcome"]).IsEqualTo("succeeded");

        // SmokeJob is not a job this build names, so it lands in the deliberate fallback bucket
        // rather than minting a series from a runtime type name.
        await Assert.That(measurements[0].Tags["job"]).IsEqualTo("other");
    }
}
