// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Pins the startup pass that makes metric series exist before anything has gone wrong.
/// </summary>
/// <remarks>
/// The observable half is the one worth testing: a gauge class nothing else injects is never
/// constructed, so its instrument is never created and its series never appear in Prometheus — while
/// every unit test still passes, because those tests construct the class directly. Resolving the
/// marker is what turns construction from a coincidence into a guarantee.
/// </remarks>
public sealed class MetricSeriesInitialiserTests
{
    [Test]
    public async Task StartAsync_ResolvesEveryObservableMetricClass()
    {
        // Enumerating the registered markers is the entire mechanism: it is what forces the
        // constructor — and therefore the observable instrument — into existence.
        CountingObservableMetrics observable = new();
        MetricSeriesInitialiser initialiser = new(
            [],
            [observable],
            NullLogger<MetricSeriesInitialiser>.Instance);

        await initialiser.StartAsync(CancellationToken.None);

        await Assert.That(observable.WasResolved).IsTrue();
    }

    [Test]
    public async Task StartAsync_InitialisesEveryCounterClass()
    {
        RecordingInitialisableMetrics metrics = new();
        MetricSeriesInitialiser initialiser = new(
            [metrics],
            [],
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
        MetricSeriesInitialiser initialiser = new(
            [throwing, healthy],
            [],
            NullLogger<MetricSeriesInitialiser>.Instance);

        await initialiser.StartAsync(CancellationToken.None);

        await Assert.That(healthy.InitialiseCount).IsEqualTo(1);
    }

    [Test]
    public async Task StopAsync_DoesNothingAndCompletes()
    {
        MetricSeriesInitialiser initialiser = new([], [], NullLogger<MetricSeriesInitialiser>.Instance);

        await initialiser.StopAsync(CancellationToken.None);

        await Assert.That(initialiser).IsNotNull();
    }
}
