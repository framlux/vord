// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Hangfire;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Test.Infrastructure;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Storage;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Test.Observability;

/// <summary>
/// Pins the job instruments. The health gauge's dense shape is the load-bearing property: one series
/// per job carrying its current state as a tag would make the previous state's series vanish on
/// transition, and an alert rule cannot evaluate a series that no longer exists.
/// </summary>
public sealed class JobMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static (JobMetrics Metrics, IMeterFactory Factory) Build(
        JobStorage storage,
        bool selfHosted = false,
        string bucketName = "exports",
        ObservabilityHost host = ObservabilityHost.ServicesWorker)
    {
        IMeterFactory factory = TestMetricsFactory.CreateMeterFactory();
        TestServiceScopeFactory scopeFactory = new(
            null!,
            new Dictionary<Type, object> { [typeof(JobStorage)] = storage });

        JobMetrics metrics = new(
            factory,
            scopeFactory,
            new FakeTimeProvider(Now),
            new DeploymentMode(Options.Create(new DeploymentOptions { SelfHosted = selfHosted })),
            Options.Create(new ObjectStorageOptions { BucketName = bucketName }),
            host,
            NullLogger<JobMetrics>.Instance);

        return (metrics, factory);
    }

    private static InMemoryStorage StorageWithAllNineScheduled()
    {
        InMemoryStorage storage = new();

        foreach (string id in RecurringJobIds.All)
        {
            WriteRecurringJob(storage, id, Now.UtcDateTime.AddMinutes(10));
        }

        return storage;
    }

    private static void WriteRecurringJob(InMemoryStorage storage, string id, DateTime? nextExecution)
    {
        Dictionary<string, string> hash = new()
        {
            ["Cron"] = "* * * * *",
            ["Job"] = InvocationData
                .SerializeJob(Job.FromExpression<ProbeJob>(j => j.RunAsync(CancellationToken.None)))
                .SerializePayload(),
            ["TimeZoneId"] = TimeZoneInfo.Utc.Id,
        };

        if (nextExecution.HasValue)
        {
            hash["NextExecution"] = JobHelper.SerializeDateTime(nextExecution.Value);
        }

        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.AddToSet("recurring-jobs", id, 0);
        transaction.SetRangeInHash($"recurring-job:{id}", hash);
        transaction.Commit();
    }

    [Test]
    public async Task HealthGauge_EmitsOneMeasurementPerIntendedJobAndState()
    {
        // Nine intended jobs by seven health states. Only the hosted deployment with object storage
        // intends all nine, so the configuration is pinned explicitly rather than defaulted — the
        // SelfHosted flag defaults to true, which would yield six jobs and forty-two measurements.
        (JobMetrics metrics, IMeterFactory factory) = Build(StorageWithAllNineScheduled());
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.jobs.recurring.health");

        collector.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(63);
        await Assert.That(measurements.Count(measurement => measurement.Value == 1L)).IsEqualTo(9);
    }

    [Test]
    public async Task HealthGauge_SelfHostedOmitsTheBillingSyncJob()
    {
        // Self-hosted does not register the billing sync. Reporting it as missing would page an
        // operator hourly, forever, about a job that is absent on purpose.
        (JobMetrics metrics, IMeterFactory factory) = Build(StorageWithAllNineScheduled(), selfHosted: true);
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.jobs.recurring.health");

        collector.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(56);
        await Assert.That(measurements.Any(measurement =>
            (string?)measurement.Tags["job_id"] == "stripe_sync")).IsFalse();
    }

    [Test]
    public async Task HealthGauge_JobIdIsTheSnakeCasedVocabularyNotTheKebabRecurringId()
    {
        // job_id and the duration histogram's job tag must be the same spelling, or the two
        // instruments cannot be joined and a rule written to the documented convention finds
        // nothing at all.
        (JobMetrics metrics, IMeterFactory factory) = Build(StorageWithAllNineScheduled());
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.jobs.recurring.health");

        collector.RecordObservableInstruments();

        List<string?> jobIds = collector.GetMeasurementSnapshot()
            .Select(measurement => (string?)measurement.Tags["job_id"])
            .Distinct()
            .ToList();

        await Assert.That(jobIds).Contains("alert_evaluation");
        await Assert.That(jobIds.Any(id => id!.Contains('-', StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task HealthGauge_MultiWordStatesAreSnakeCased()
    {
        (JobMetrics metrics, IMeterFactory factory) = Build(StorageWithAllNineScheduled());
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.jobs.recurring.health");

        collector.RecordObservableInstruments();

        List<string?> states = collector.GetMeasurementSnapshot()
            .Select(measurement => (string?)measurement.Tags["health"])
            .Distinct()
            .ToList();

        await Assert.That(states).Contains("load_failed");
        await Assert.That(states).Contains("scheduling_error");
    }

    [Test]
    public async Task HealthGauge_MissingJobIsReportedRatherThanOmitted()
    {
        // An intended job absent from storage is the schedule having been lost, which is exactly
        // what the rule watches for — so it must still produce its seven series.
        InMemoryStorage storage = new();
        foreach (string id in RecurringJobIds.All.Where(id => id != RecurringJobIds.TenantPurge))
        {
            WriteRecurringJob(storage, id, Now.UtcDateTime.AddMinutes(10));
        }

        (JobMetrics metrics, IMeterFactory factory) = Build(storage);
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.jobs.recurring.health");

        collector.RecordObservableInstruments();

        CollectedMeasurement<long> missing = collector.GetMeasurementSnapshot()
            .Single(measurement =>
                ((string?)measurement.Tags["job_id"] == "tenant_purge") &&
                ((string?)measurement.Tags["health"] == "missing"));

        await Assert.That(missing.Value).IsEqualTo(1L);
    }

    [Test]
    public async Task QueueDepthGauge_EmitsAZeroForEveryKnownQueue()
    {
        // An idle queue reports no row from storage. Iterating the enum rather than the storage
        // result is what keeps the series alive while the system is healthy — the same reason the
        // fleet gauge iterates its status enum.
        (JobMetrics metrics, IMeterFactory factory) = Build(new InMemoryStorage());
        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.jobs.queue_depth");

        collector.RecordObservableInstruments();

        IReadOnlyList<CollectedMeasurement<long>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(4);
        await Assert.That(measurements.All(measurement => measurement.Value == 0L)).IsTrue();

        List<string?> queues = measurements
            .Select(measurement => (string?)measurement.Tags["queue"])
            .ToList();

        await Assert.That(queues).Contains("critical");
        await Assert.That(queues).Contains("default");
        await Assert.That(queues).Contains("long_running");
        await Assert.That(queues).Contains("other");
    }

    [Test]
    public async Task RecordRun_TagsJobAndOutcomeAndRecordsSeconds()
    {
        (JobMetrics metrics, IMeterFactory factory) = Build(new InMemoryStorage());
        using MetricCollector<double> collector = new(factory, VordMeter.Name, "vord.jobs.duration_seconds");

        metrics.RecordRun(InstrumentedJob.AlertEvaluation, JobOutcome.Succeeded, 1.5d);

        IReadOnlyList<CollectedMeasurement<double>> measurements = collector.GetMeasurementSnapshot();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Value).IsEqualTo(1.5d);
        await Assert.That(measurements[0].Tags["job"]).IsEqualTo("alert_evaluation");
        await Assert.That(measurements[0].Tags["outcome"]).IsEqualTo("succeeded");
    }

    [Test]
    public async Task Gauges_OnTheApiServer_YieldNoMeasurement()
    {
        // The Hangfire filter constructs this class in both hosts, so the gauges gate themselves.
        // Without that, every api-server replica would query Hangfire storage on each collection
        // cycle and duplicate every series.
        (JobMetrics metrics, IMeterFactory factory) = Build(
            StorageWithAllNineScheduled(), host: ObservabilityHost.ApiServer);

        using MetricCollector<long> health = new(factory, VordMeter.Name, "vord.jobs.recurring.health");
        using MetricCollector<long> depth = new(factory, VordMeter.Name, "vord.jobs.queue_depth");

        health.RecordObservableInstruments();
        depth.RecordObservableInstruments();

        await Assert.That(health.GetMeasurementSnapshot().Count).IsEqualTo(0);
        await Assert.That(depth.GetMeasurementSnapshot().Count).IsEqualTo(0);
    }

    [Test]
    public async Task HealthGauge_WhenStorageThrows_YieldsNoMeasurement()
    {
        // One unhealthy query must not abort the collection cycle and blind every other instrument.
        TestServiceScopeFactory emptyScope = new(null!, []);
        IMeterFactory factory = TestMetricsFactory.CreateMeterFactory();
        JobMetrics metrics = new(
            factory,
            emptyScope,
            new FakeTimeProvider(Now),
            new DeploymentMode(Options.Create(new DeploymentOptions { SelfHosted = false })),
            Options.Create(new ObjectStorageOptions { BucketName = "exports" }),
            ObservabilityHost.ServicesWorker,
            NullLogger<JobMetrics>.Instance);

        using MetricCollector<long> collector = new(factory, VordMeter.Name, "vord.jobs.recurring.health");

        collector.RecordObservableInstruments();

        await Assert.That(collector.GetMeasurementSnapshot().Count).IsEqualTo(0);
        await Assert.That(metrics).IsNotNull();
    }

    /// <summary>A stand-in job type whose serialized payload only has to deserialise.</summary>
    public sealed class ProbeJob
    {
        /// <summary>Never invoked.</summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A completed task.</returns>
        public Task RunAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
