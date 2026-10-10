// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Diagnostics.Metrics;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Test.Infrastructure;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="QuotaCutoffDisplay"/>: a page that cannot learn which hosts are paused still
/// renders, shows them as active, and says so on the display failure counter and in the log.
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
        FakeLogger logger = new();
        using (failures)
        {
            QuotaCutoff cutoff = await QuotaCutoffDisplay.GetOrNoneAsync(
                TestQuotaPause.With(QuotaCutoff.After(42), 3), metrics, logger, 7, CancellationToken.None);

            await Assert.That(cutoff).IsEqualTo(QuotaCutoff.After(42));
            await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
            await Assert.That(logger.Collector.GetSnapshot().Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task GetOrNoneAsync_DecisionUnavailable_ReturnsNoneAndRecordsOneDisplayFailure()
    {
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        using (failures)
        {
            QuotaCutoff cutoff = await QuotaCutoffDisplay.GetOrNoneAsync(
                TestQuotaPause.Unavailable(), metrics, NullLogger.Instance, 7, CancellationToken.None);

            await Assert.That(cutoff).IsEqualTo(QuotaCutoff.None);
            IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
            await Assert.That(measurements.Count).IsEqualTo(1);
            await Assert.That(measurements[0].Value).IsEqualTo(1L);
            await Assert.That(measurements[0].Tags["surface"]).IsEqualTo("display");
        }
    }

    [Test]
    public async Task GetOrNoneAsync_DecisionUnavailable_LogsOneWarningWithTheCauseAndTheTenant()
    {
        // The counter says that checks are failing; the log is where an operator reads why.
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        FakeLogger logger = new();
        using (failures)
        {
            await QuotaCutoffDisplay.GetOrNoneAsync(TestQuotaPause.Unavailable(), metrics, logger, 7, CancellationToken.None);

            IReadOnlyList<FakeLogRecord> warnings = [.. logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Warning)];
            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Message).Contains("7");
            await Assert.That(warnings[0].Exception).IsTypeOf<QuotaCheckUnavailableException>();
            await Assert.That(warnings[0].Exception!.InnerException!.Message).IsEqualTo("database unavailable");
        }
    }

    [Test]
    public async Task GetOrNoneAsync_Cancelled_PropagatesWithoutLoggingOrCounting()
    {
        // A cancelled request is not an unavailable decision, and must not be reported as a failure.
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        FakeLogger logger = new();
        using (failures)
        {
            IQuotaPauseService quotaPause = Substitute.For<IQuotaPauseService>();
            quotaPause.GetCutoffAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns<Task<QuotaCutoff>>(_ => throw new OperationCanceledException());

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await QuotaCutoffDisplay.GetOrNoneAsync(quotaPause, metrics, logger, 7, CancellationToken.None));
            await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
            await Assert.That(logger.Collector.GetSnapshot().Count).IsEqualTo(0);
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
                await QuotaCutoffDisplay.GetOrNoneAsync(quotaPause, metrics, NullLogger.Instance, 7, CancellationToken.None));
            await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task GetOrNoneAsync_NullQuotaPause_Throws()
    {
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        using (failures)
        {
            ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await QuotaCutoffDisplay.GetOrNoneAsync(null!, metrics, NullLogger.Instance, 7, CancellationToken.None));

            await Assert.That(ex!.ParamName).IsEqualTo("quotaPause");
        }
    }

    [Test]
    public async Task GetOrNoneAsync_NullMetrics_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await QuotaCutoffDisplay.GetOrNoneAsync(TestQuotaPause.Nothing(), null!, NullLogger.Instance, 7, CancellationToken.None));

        await Assert.That(ex!.ParamName).IsEqualTo("metrics");
    }

    [Test]
    public async Task GetOrNoneAsync_NullLogger_Throws()
    {
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        using (failures)
        {
            ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await QuotaCutoffDisplay.GetOrNoneAsync(TestQuotaPause.Nothing(), metrics, null!, 7, CancellationToken.None));

            await Assert.That(ex!.ParamName).IsEqualTo("logger");
        }
    }

    [Test]
    public async Task GetPausedCountOrZeroAsync_DecisionAvailable_ReturnsTheCount()
    {
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        FakeLogger logger = new();
        using (failures)
        {
            int count = await QuotaCutoffDisplay.GetPausedCountOrZeroAsync(
                TestQuotaPause.With(QuotaCutoff.None, 4), metrics, logger, 7, CancellationToken.None);

            await Assert.That(count).IsEqualTo(4);
            await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
            await Assert.That(logger.Collector.GetSnapshot().Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task GetPausedCountOrZeroAsync_DecisionUnavailable_ReturnsZeroAndRecordsOneDisplayFailure()
    {
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        using (failures)
        {
            int count = await QuotaCutoffDisplay.GetPausedCountOrZeroAsync(
                TestQuotaPause.Unavailable(), metrics, NullLogger.Instance, 7, CancellationToken.None);

            await Assert.That(count).IsEqualTo(0);
            IReadOnlyList<CollectedMeasurement<long>> measurements = failures.GetMeasurementSnapshot();
            await Assert.That(measurements.Count).IsEqualTo(1);
            await Assert.That(measurements[0].Tags["surface"]).IsEqualTo("display");
        }
    }

    [Test]
    public async Task GetPausedCountOrZeroAsync_DecisionUnavailable_LogsOneWarningWithTheCauseAndTheTenant()
    {
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        FakeLogger logger = new();
        using (failures)
        {
            await QuotaCutoffDisplay.GetPausedCountOrZeroAsync(TestQuotaPause.Unavailable(), metrics, logger, 7, CancellationToken.None);

            IReadOnlyList<FakeLogRecord> warnings = [.. logger.Collector.GetSnapshot().Where(r => r.Level == LogLevel.Warning)];
            await Assert.That(warnings.Count).IsEqualTo(1);
            await Assert.That(warnings[0].Message).Contains("7");
            await Assert.That(warnings[0].Exception).IsTypeOf<QuotaCheckUnavailableException>();
        }
    }

    [Test]
    public async Task GetPausedCountOrZeroAsync_Cancelled_PropagatesWithoutLoggingOrCounting()
    {
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        FakeLogger logger = new();
        using (failures)
        {
            IQuotaPauseService quotaPause = Substitute.For<IQuotaPauseService>();
            quotaPause.GetPausedCountAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns<Task<int>>(_ => throw new OperationCanceledException());

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await QuotaCutoffDisplay.GetPausedCountOrZeroAsync(quotaPause, metrics, logger, 7, CancellationToken.None));
            await Assert.That(failures.GetMeasurementSnapshot().Count).IsEqualTo(0);
            await Assert.That(logger.Collector.GetSnapshot().Count).IsEqualTo(0);
        }
    }

    [Test]
    public async Task GetPausedCountOrZeroAsync_NullQuotaPause_Throws()
    {
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        using (failures)
        {
            ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await QuotaCutoffDisplay.GetPausedCountOrZeroAsync(null!, metrics, NullLogger.Instance, 7, CancellationToken.None));

            await Assert.That(ex!.ParamName).IsEqualTo("quotaPause");
        }
    }

    [Test]
    public async Task GetPausedCountOrZeroAsync_NullMetrics_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await QuotaCutoffDisplay.GetPausedCountOrZeroAsync(TestQuotaPause.Nothing(), null!, NullLogger.Instance, 7, CancellationToken.None));

        await Assert.That(ex!.ParamName).IsEqualTo("metrics");
    }

    [Test]
    public async Task GetPausedCountOrZeroAsync_NullLogger_Throws()
    {
        (QuotaPauseMetrics metrics, MetricCollector<long> failures) = BuildMetrics();
        using (failures)
        {
            ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(async () =>
                await QuotaCutoffDisplay.GetPausedCountOrZeroAsync(TestQuotaPause.Nothing(), metrics, null!, 7, CancellationToken.None));

            await Assert.That(ex!.ParamName).IsEqualTo("logger");
        }
    }
}
