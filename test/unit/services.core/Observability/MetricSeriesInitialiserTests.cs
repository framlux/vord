// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Pins the startup pass that makes metric series exist before anything has gone wrong.
/// </summary>
/// <remarks>
/// The observable half is the one worth testing: a gauge class nothing else injects is never
/// constructed, so its instrument is never created and its series never appear in Prometheus — while
/// every unit test still passes, because those tests construct the class directly. Resolving the
/// descriptors here is what turns construction from a coincidence into a guarantee, and doing the
/// resolving inside the pass is what keeps one unbuildable gauge from costing the whole process.
/// </remarks>
public sealed class MetricSeriesInitialiserTests
{
    [Test]
    public async Task StartAsync_WhenOneGaugeClassCannotBeConstructed_StillConstructsTheRest()
    {
        // A gauge whose constructor fails should cost its own series and nothing else. If the
        // classes were injected instead of resolved here, the container would build them before
        // this method ran and the process would never start at all.
        MetricsConstructionLog log = new();
        ServiceCollection services = new();
        services.AddSingleton(log);
        services.AddSingleton<ThrowingObservableMetrics>();
        services.AddSingleton(ObservableMetricsDescriptor.For<ThrowingObservableMetrics>());
        services.AddSingleton<CountingObservableMetrics>();
        services.AddSingleton(ObservableMetricsDescriptor.For<CountingObservableMetrics>());

        using ServiceProvider provider = services.BuildServiceProvider();

        MetricSeriesInitialiser initialiser = new(
            [],
            provider.GetServices<ObservableMetricsDescriptor>(),
            provider,
            NullLogger<MetricSeriesInitialiser>.Instance);

        await initialiser.StartAsync(CancellationToken.None);

        await Assert.That(log.Constructed.Count).IsEqualTo(1);
        await Assert.That(log.Constructed[0]).IsEqualTo(nameof(CountingObservableMetrics));
    }

    [Test]
    public async Task StartAsync_InitialisesEveryCounterClass()
    {
        RecordingInitialisableMetrics metrics = new();
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        MetricSeriesInitialiser initialiser = new(
            [metrics],
            [],
            provider,
            NullLogger<MetricSeriesInitialiser>.Instance);

        await initialiser.StartAsync(CancellationToken.None);

        await Assert.That(metrics.InitialiseCount).IsEqualTo(1);
    }

    [Test]
    public async Task StartAsync_WhenOneClassThrows_StillInitialisesTheRest()
    {
        // Losing pre-recorded zeros costs first-failure detection, not the correctness of the
        // process, so one bad class must not stop startup or starve its neighbours.
        ThrowingInitialisableMetrics throwing = new();
        RecordingInitialisableMetrics healthy = new();
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        MetricSeriesInitialiser initialiser = new(
            [throwing, healthy],
            [],
            provider,
            NullLogger<MetricSeriesInitialiser>.Instance);

        await initialiser.StartAsync(CancellationToken.None);

        await Assert.That(healthy.InitialiseCount).IsEqualTo(1);
    }

    [Test]
    public async Task StopAsync_DoesNothingAndCompletes()
    {
        using ServiceProvider provider = new ServiceCollection().BuildServiceProvider();
        MetricSeriesInitialiser initialiser = new(
            [],
            [],
            provider,
            NullLogger<MetricSeriesInitialiser>.Instance);

        await initialiser.StopAsync(CancellationToken.None);

        await Assert.That(initialiser).IsNotNull();
    }
}
