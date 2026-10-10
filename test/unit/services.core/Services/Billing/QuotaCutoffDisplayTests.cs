// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using NSubstitute;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="QuotaCutoffDisplay"/>: a page that cannot learn which hosts are paused still
/// renders, shows them as active, and says so on the display failure counter.
/// </summary>
public sealed class QuotaCutoffDisplayTests
{
    private static (QuotaPauseMetrics Metrics, MetricCollector<long> Failures) BuildMetrics()
    {
        IMeterFactory factory = TestMetricsFactory.CreateMeterFactory();

        return (new QuotaPauseMetrics(factory), new MetricCollector<long>(factory, VordMeter.Name, "vord.quota.pause_check_failures"));
    }

    [Test]
    public async Task GetOrNoneAsync_DecisionAvailable_ReturnsItAndRecordsNothing()
    {
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        using (failures)
        {
            QuotaCutoff cutoff = await QuotaCutoffDisplay.GetOrNoneAsync(
                TestQuotaPause.With(QuotaCutoff.After(42), 3), metrics, 7, CancellationToken.None);

            await Assert.That(cutoff).IsEqualTo(QuotaCutoff.After(42));
            await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task GetOrNoneAsync_DecisionUnavailable_ReturnsNoneAndRecordsOneDisplayFailure()
    {
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        using (failures)
        {
            QuotaCutoff cutoff = await QuotaCutoffDisplay.GetOrNoneAsync(
                TestQuotaPause.Unavailable(), metrics, 7, CancellationToken.None);

            await Assert.That(cutoff).IsEqualTo(QuotaCutoff.None);
            IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
            await Assert.That(measurements.Count).IsEqualTo(1);
            await Assert.That(measurements[0].Value).IsEqualTo(1L);
            await Assert.That(measurements[0].Tags["surface"]).IsEqualTo("display");
        }
    }

    [Test]
    public async Task GetOrNoneAsync_Cancelled_Propagates()
    {
        // A cancelled request is not an unavailable decision, and must not be reported as a failure.
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        using (failures)
        {
            IQuotaPauseService quotaPause = Substitute.For<IQuotaPauseService>();
            quotaPause.GetCutoffAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns<Task<QuotaCutoff>>(_ => throw new OperationCanceledException());

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await QuotaCutoffDisplay.GetOrNoneAsync(quotaPause, metrics, 7, CancellationToken.None));
            await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task GetOrNoneAsync_UnrelatedFailure_Propagates()
    {
        // Only the pause service's own unavailable signal is absorbed; a bug elsewhere must stay loud.
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        using (failures)
        {
            IQuotaPauseService quotaPause = Substitute.For<IQuotaPauseService>();
            quotaPause.GetCutoffAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns<Task<QuotaCutoff>>(_ => throw new InvalidOperationException("unrelated"));

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await QuotaCutoffDisplay.GetOrNoneAsync(quotaPause, metrics, 7, CancellationToken.None));
            await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task GetPausedCountOrZeroAsync_DecisionAvailable_ReturnsTheCount()
    {
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        using (failures)
        {
            int count = await QuotaCutoffDisplay.GetPausedCountOrZeroAsync(
                TestQuotaPause.With(QuotaCutoff.None, 4), metrics, 7, CancellationToken.None);

            await Assert.That(count).IsEqualTo(4);
            await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task GetPausedCountOrZeroAsync_DecisionUnavailable_ReturnsZeroAndRecordsOneDisplayFailure()
    {
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        using (failures)
        {
            int count = await QuotaCutoffDisplay.GetPausedCountOrZeroAsync(
                TestQuotaPause.Unavailable(), metrics, 7, CancellationToken.None);

            await Assert.That(count).IsEqualTo(0);
            IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
            await Assert.That(measurements.Count).IsEqualTo(1);
            await Assert.That(measurements[0].Tags["surface"]).IsEqualTo("display");
        }
    }

    [Test]
    public async Task GetPausedCountOrZeroAsync_Cancelled_Propagates()
    {
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        using (failures)
        {
            IQuotaPauseService quotaPause = Substitute.For<IQuotaPauseService>();
            quotaPause.GetPausedCountAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns<Task<int>>(_ => throw new OperationCanceledException());

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await QuotaCutoffDisplay.GetPausedCountOrZeroAsync(quotaPause, metrics, 7, CancellationToken.None));
            await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
        }
    }
}
