// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;

namespace Framlux.FleetManagement.Services.Core.Hangfire;

/// <summary>
/// Reads recurring-job state from Hangfire storage and reconciles it against the set of jobs this
/// build knows about, so an operator can tell a healthy schedule from a stalled or absent one.
/// </summary>
/// <remarks>
/// Storage is taken as a constructor parameter rather than read from <c>JobStorage.Current</c>.
/// Note this does not by itself isolate concurrent test hosts — Hangfire's DI registration is a
/// factory returning that same process-global — but it does let a unit test supply its own
/// storage directly.
/// </remarks>
public sealed class RecurringJobInspector
{
    /// <summary>
    /// How far past its next occurrence a job may drift before it is reported as overdue. Must
    /// exceed Hangfire's 15-second schedule-polling interval by enough margin that ordinary
    /// jitter on a per-minute job does not raise a false alarm.
    /// </summary>
    public static readonly TimeSpan OverdueGrace = TimeSpan.FromMinutes(2);

    private readonly JobStorage _storage;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a new instance of the <see cref="RecurringJobInspector"/> class.
    /// </summary>
    /// <param name="storage">The Hangfire storage to read.</param>
    /// <param name="timeProvider">Clock used to evaluate the overdue window.</param>
    public RecurringJobInspector(JobStorage storage, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _storage = storage;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Returns one snapshot per id in <see cref="RecurringJobIds.All"/>, in registration order.
    /// Ids present in storage but unknown to this build are ignored: they are stale registrations
    /// from an older release and reporting them would be noise.
    /// </summary>
    /// <returns>A snapshot per known recurring job.</returns>
    public IReadOnlyList<RecurringJobSnapshot> Inspect()
    {
        using IStorageConnection connection = _storage.GetConnection();
        List<RecurringJobDto> stored = connection.GetRecurringJobs();
        Dictionary<string, RecurringJobDto> byId = stored
            .Where(dto => dto.Id is not null)
            .GroupBy(dto => dto.Id)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        List<RecurringJobSnapshot> snapshots = new(RecurringJobIds.All.Count);

        foreach (string id in RecurringJobIds.All)
        {
            snapshots.Add(Classify(id, byId.GetValueOrDefault(id), now));
        }

        return snapshots;
    }

    /// <summary>
    /// Returns the job definition stored for a recurring job, or null when the job is absent from
    /// storage or its payload cannot be deserialised. Callers enqueue this rather than resolving a
    /// type themselves, so there is no second registry to drift from
    /// <see cref="RecurringJobRegistry"/>.
    /// </summary>
    /// <param name="recurringJobId">The recurring job id to resolve.</param>
    /// <returns>The stored job definition, or null.</returns>
    public Job? GetJobDefinition(string recurringJobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recurringJobId);

        using IStorageConnection connection = _storage.GetConnection();
        RecurringJobDto? dto = connection.GetRecurringJobs()
            .FirstOrDefault(candidate => string.Equals(candidate.Id, recurringJobId, StringComparison.Ordinal));

        if (dto is null)
        {
            return null;
        }

        if (dto.Removed)
        {
            return null;
        }

        return dto.Job;
    }

    private static RecurringJobSnapshot Classify(string id, RecurringJobDto? dto, DateTime now)
    {
        if (dto is null)
        {
            return Absent(id);
        }

        // The id is in the recurring-jobs set but its hash is gone. Hangfire reports this with a
        // null Cron, which would otherwise render as a scheduled job with a blank schedule.
        if (dto.Removed)
        {
            return Absent(id);
        }

        RecurringJobHealth status = DetermineStatus(dto, now);

        return new RecurringJobSnapshot(
            id,
            dto.Cron ?? "",
            dto.TimeZoneId ?? "",
            dto.LastExecution,
            dto.NextExecution,
            dto.LastJobId ?? "",
            dto.LastJobState ?? "",
            dto.Error ?? "",
            dto.RetryAttempt,
            status);
    }

    private static RecurringJobHealth DetermineStatus(RecurringJobDto dto, DateTime now)
    {
        if ((dto.LoadException is not null) || (dto.Job is null))
        {
            return RecurringJobHealth.LoadFailed;
        }

        // Hangfire's RecurringJobEntity.Disable sets NextExecution to null and Error to a message
        // after repeated scheduling failures. A "next execution is in the past" comparison is
        // false against null, so without this branch a permanently dead job reports as healthy.
        if (dto.NextExecution is null)
        {
            return RecurringJobHealth.Disabled;
        }

        if (dto.NextExecution.Value < (now - OverdueGrace))
        {
            return RecurringJobHealth.Overdue;
        }

        return RecurringJobHealth.Scheduled;
    }

    private static RecurringJobSnapshot Absent(string id)
    {
        return new RecurringJobSnapshot(
            id, "", "", null, null, "", "", "", 0, RecurringJobHealth.Missing);
    }
}
