// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Hangfire;

/// <summary>
/// A point-in-time view of one recurring job, combining what Hangfire stores with the health
/// classification derived from it.
/// </summary>
/// <param name="Id">The recurring job id from <see cref="RecurringJobIds"/>.</param>
/// <param name="Cron">The cron expression, empty when the job is absent from storage.</param>
/// <param name="TimeZoneId">The schedule's time zone id, empty when unknown.</param>
/// <param name="LastExecution">When the scheduler last fired this job, null until the first run.</param>
/// <param name="NextExecution">The next occurrence, null when Hangfire has disabled the job.</param>
/// <param name="LastJobId">The background job id of the last scheduled run, empty when unknown.</param>
/// <param name="LastJobState">
/// The state name of the last run. Best-effort: Hangfire resolves this by reading the job row,
/// which it expires after a day, so an aged-out daily job reports empty. Empty is not success.
/// </param>
/// <param name="Error">The scheduler's own error, not the job's failure message.</param>
/// <param name="RetryAttempt">How many consecutive times the scheduler has failed to schedule.</param>
/// <param name="Status">The derived health.</param>
public sealed record RecurringJobSnapshot(
    string Id,
    string Cron,
    string TimeZoneId,
    DateTime? LastExecution,
    DateTime? NextExecution,
    string LastJobId,
    string LastJobState,
    string Error,
    int RetryAttempt,
    RecurringJobHealth Status);
