// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Hangfire.Server;

namespace Framlux.FleetManagement.Services.Core.Hangfire;

/// <summary>
/// Records how long each background job run took and whether it threw.
/// </summary>
/// <remarks>
/// A server filter rather than a wrapper on each job: Hangfire already brackets every invocation, and
/// instrumenting each job type individually would mean a new job silently arriving with no timing at
/// all. The filter is registered in both processes because Hangfire is configured in both, but only
/// the worker runs a processing server, so only the worker ever invokes it.
/// </remarks>
public sealed class JobDurationFilter : IServerFilter
{
    private const string StartTimestampKey = "VordJobStartTimestamp";

    private readonly JobMetrics _jobMetrics;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates the filter.
    /// </summary>
    /// <param name="jobMetrics">The instruments the timings are recorded on.</param>
    /// <param name="timeProvider">The clock elapsed time is measured with.</param>
    public JobDurationFilter(JobMetrics jobMetrics, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(jobMetrics);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _jobMetrics = jobMetrics;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public void OnPerforming(PerformingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Items[StartTimestampKey] = _timeProvider.GetTimestamp();
    }

    /// <inheritdoc />
    public void OnPerformed(PerformedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if ((context.Items.TryGetValue(StartTimestampKey, out object? stored) == false) ||
            (stored is not long startTimestamp))
        {
            return;
        }

        // The recurring id is the only thing that is actually the job's id; the type name is
        // unrelated to it, and is null for a fire-and-forget job. Both sources are needed.
        InstrumentedJob job = InstrumentedJobMap.Resolve(
            context.GetJobParameter<string>("RecurringJobId"),
            context.BackgroundJob?.Job?.Type?.Name);

        JobOutcome outcome = context.Exception is null ? JobOutcome.Succeeded : JobOutcome.Failed;

        _jobMetrics.RecordRun(job, outcome, _timeProvider.GetElapsedTime(startTimestamp).TotalSeconds);
    }
}
