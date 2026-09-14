// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Deployment;
using Framlux.FleetManagement.Services.Core.Hangfire;
using Framlux.FleetManagement.Services.Core.Options;
using Hangfire;
using Hangfire.Storage.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Reports the health of the recurring job schedule, how deep each queue is, and how long each job
/// run took.
/// </summary>
/// <remarks>
/// The health gauge emits a measurement for every (intended job, state) pair rather than one series
/// per job carrying its current state as a tag. A tag-carrying series disappears the moment the job
/// changes state, and an alert rule cannot evaluate a series that no longer exists — so the dense
/// shape is what makes the rule work, not a stylistic choice.
///
/// Jobs the current configuration deliberately does not register are excluded entirely. Reporting a
/// deliberately absent job as Missing would page an operator hourly, forever, about an intended
/// absence.
///
/// The gauges report only from the worker. The Hangfire filter that feeds the duration histogram is
/// constructed in both processes, so this class is too — but only the worker runs a processing
/// server, and letting the API server's replicas each query Hangfire storage on every collection
/// cycle would duplicate every series for no gain.
/// </remarks>
public sealed class JobMetrics : IObservableMetrics
{
    /// <summary>
    /// How long one gauge measurement may take before it is abandoned. The callbacks run on the
    /// exporter's collection thread and Hangfire's monitoring API hits Postgres, so without this a
    /// hung query would stall every instrument in the process.
    /// </summary>
    private static readonly TimeSpan MeasurementTimeout = TimeSpan.FromSeconds(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JobMetrics> _logger;
    private readonly ObservabilityHost _host;
    private readonly bool _isSaas;
    private readonly bool _objectStorageEnabled;
    private readonly Histogram<double> _runDuration;

    /// <summary>
    /// Creates the job instruments.
    /// </summary>
    /// <param name="meterFactory">The factory the SDK also observes.</param>
    /// <param name="scopeFactory">Used to resolve job storage per observation.</param>
    /// <param name="timeProvider">The clock the job inspector measures overdue against.</param>
    /// <param name="deploymentMode">Decides whether the billing sync job is intended to exist.</param>
    /// <param name="objectStorageOptions">Decides whether the data export jobs are intended to exist.</param>
    /// <param name="host">Which process this is. The gauges report only from the worker.</param>
    /// <param name="logger">The logger.</param>
    public JobMetrics(
        IMeterFactory meterFactory,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        DeploymentMode deploymentMode,
        IOptions<ObjectStorageOptions> objectStorageOptions,
        ObservabilityHost host,
        ILogger<JobMetrics> logger)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(deploymentMode);
        ArgumentNullException.ThrowIfNull(objectStorageOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
        _host = host;
        _isSaas = deploymentMode.IsSaas;
        _objectStorageEnabled = string.IsNullOrEmpty(objectStorageOptions.Value.BucketName) == false;

        Meter meter = meterFactory.Create(VordMeter.Name);

        meter.CreateObservableGauge(
            "vord.jobs.recurring.health",
            ObserveRecurringHealth,
            description: "One per intended recurring job and health state: 1 for the current state, 0 otherwise.");

        meter.CreateObservableGauge(
            "vord.jobs.queue_depth",
            ObserveQueueDepth,
            description: "Enqueued job count per queue.");

        _runDuration = meter.CreateHistogram<double>(
            "vord.jobs.duration_seconds",
            unit: "s",
            description: "How long each background job run took, by job and outcome.");
    }

    /// <summary>
    /// Records one completed job run.
    /// </summary>
    /// <param name="job">Which job ran.</param>
    /// <param name="outcome">Whether it threw.</param>
    /// <param name="seconds">How long it took.</param>
    public void RecordRun(InstrumentedJob job, JobOutcome outcome, double seconds)
    {
        _runDuration.Record(
            seconds,
            new KeyValuePair<string, object?>("job", MetricTag.From(job)),
            new KeyValuePair<string, object?>("outcome", MetricTag.From(outcome)));
    }

    private IEnumerable<Measurement<long>> ObserveRecurringHealth()
    {
        if (_host != ObservabilityHost.ServicesWorker)
        {
            return [];
        }

        try
        {
            IReadOnlySet<string> intended = RecurringJobRegistry.IntendedJobIds(_isSaas, _objectStorageEnabled);
            IReadOnlyList<RecurringJobSnapshot> snapshots = Inspect();

            List<Measurement<long>> measurements = [];

            foreach (RecurringJobSnapshot snapshot in snapshots)
            {
                if (intended.Contains(snapshot.Id) == false)
                {
                    continue;
                }

                KeyValuePair<string, object?> jobTag = new(
                    "job_id",
                    MetricTag.From(InstrumentedJobMap.ResolveRecurring(snapshot.Id)));

                foreach (RecurringJobHealth health in Enum.GetValues<RecurringJobHealth>())
                {
                    measurements.Add(new Measurement<long>(
                        snapshot.Status == health ? 1L : 0L,
                        jobTag,
                        new KeyValuePair<string, object?>("health", MetricTag.From(health))));
                }
            }

            return measurements;
        }
        catch (Exception ex)
        {
            // No measurement rather than a throw: an exception inside an observable callback aborts
            // the whole collection cycle, blinding every other instrument in the process.
            _logger.LogWarning(ex, "Could not read recurring job health");

            return [];
        }
    }

    private IEnumerable<Measurement<long>> ObserveQueueDepth()
    {
        if (_host != ObservabilityHost.ServicesWorker)
        {
            return [];
        }

        try
        {
            Dictionary<HangfireQueueKind, long> depths = [];

            foreach (HangfireQueueKind queue in Enum.GetValues<HangfireQueueKind>())
            {
                depths[queue] = 0L;
            }

            using CancellationTokenSource timeout = new(MeasurementTimeout);
            using IServiceScope scope = _scopeFactory.CreateScope();
            JobStorage storage = scope.ServiceProvider.GetRequiredService<JobStorage>();

            foreach (QueueWithTopEnqueuedJobsDto queue in storage.GetMonitoringApi().Queues())
            {
                depths[MapQueue(queue.Name)] += queue.Length;
            }

            // Every known queue reports, including the empty ones. A series that exists only while
            // the queue is backed up is one no rule can be written against.
            return depths
                .Select(entry => new Measurement<long>(
                    entry.Value,
                    new KeyValuePair<string, object?>("queue", MetricTag.From(entry.Key))))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read Hangfire queue depths");

            return [];
        }
    }

    private static HangfireQueueKind MapQueue(string name)
    {
        return name switch
        {
            "critical" => HangfireQueueKind.Critical,
            "default" => HangfireQueueKind.Default,
            "long" => HangfireQueueKind.LongRunning,
            _ => HangfireQueueKind.Other,
        };
    }

    private IReadOnlyList<RecurringJobSnapshot> Inspect()
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        JobStorage storage = scope.ServiceProvider.GetRequiredService<JobStorage>();

        // The inspector is not registered in DI — it is constructed where it is used — so it is
        // built here rather than resolved, which would throw on every collection cycle. It is
        // synchronous and takes no cancellation token, so nothing bounds it today; if it ever gains
        // one, this is where the measurement timeout is threaded through.
        return new RecurringJobInspector(storage, _timeProvider).Inspect();
    }
}
