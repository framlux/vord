// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Repositories;
using Hangfire;

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// Hangfire job that moves a tenant's surviving telemetry into the retention class its current
/// subscription maps to. Enqueued after any change to a tenant's effective retention — a tier change
/// in either direction, or a per-tenant retention override created, updated, or removed — because
/// customers do not remember which machines ran under which plan: surviving data must follow the
/// current subscription.
/// </summary>
/// <remarks>
/// <para>
/// The target class is re-resolved from the tenant's current effective retention when the job runs,
/// never captured at enqueue, so two changes in quick succession converge on the latest state.
/// </para>
/// <para>
/// Only rows inside the NEW effective window move. Rows older than it keep their old class: their
/// target class already dropped those days, so moving them would either fabricate expired partitions
/// or silently destroy the data. Leaving them where they are preserves the accidental-downgrade undo
/// — re-upgrading within the old window brings the history back — while the rows stay query-hidden
/// and expire on the old schedule.
/// </para>
/// <para>
/// There is no per-tenant concurrency lock. Hangfire's <c>DisableConcurrentExecution</c> keys on the
/// job method rather than its arguments, so it would serialize unrelated tenants, and the codebase's
/// per-tenant advisory lock is try-once — a skipped run would drop the reclassification entirely.
/// Overlap is instead made safe by the repository's "class differs from target" guard, which makes
/// every UPDATE idempotent, and by resolving the target at execution time.
/// </para>
/// </remarks>
public sealed class RetentionReclassifyJob
{
    /// <summary>
    /// Days beyond the current instant that the day-chunk walk covers. The ingest path clamps a row's
    /// partition-key timestamp to the pre-created future partitions, so a clock-skewed agent can place
    /// a row up to this many days ahead; those rows must move with the rest.
    /// </summary>
    internal const int FutureWindowDays = 7;

    private readonly ISubscriptionRepository _subscriptionRepository;
    private readonly IMachineStateRepository _machineStateRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RetentionReclassifyJob> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="RetentionReclassifyJob"/> class.
    /// </summary>
    /// <param name="subscriptionRepository">Source of the tenant's current effective retention.</param>
    /// <param name="machineStateRepository">Repository performing the day-bounded reclassify update.</param>
    /// <param name="timeProvider">Clock used to bound the reclassification window.</param>
    /// <param name="logger">The logger.</param>
    public RetentionReclassifyJob(
        ISubscriptionRepository subscriptionRepository,
        IMachineStateRepository machineStateRepository,
        TimeProvider timeProvider,
        ILogger<RetentionReclassifyJob> logger)
    {
        ArgumentNullException.ThrowIfNull(subscriptionRepository);
        ArgumentNullException.ThrowIfNull(machineStateRepository);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _subscriptionRepository = subscriptionRepository;
        _machineStateRepository = machineStateRepository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Moves the tenant's in-window telemetry into the class its current effective retention maps to.
    /// Runs on the default queue: it is admin- and billing-initiated work that must not compete with
    /// the per-minute critical jobs, and it is short enough not to belong on the long queue.
    /// </summary>
    /// <param name="tenantId">The tenant whose telemetry is reclassified. Must be positive.</param>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    [Queue("default")]
    public async Task RunAsync(int tenantId, CancellationToken ct)
    {
        if (tenantId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tenantId), "Tenant id must be positive.");
        }

        int retentionDays = await _subscriptionRepository.GetEffectiveRetentionDaysAsync(tenantId, ct);
        RetentionClass target = RetentionClassPolicy.Classify(retentionDays);
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> chunks =
            BuildDayChunks(_timeProvider.GetUtcNow(), retentionDays);

        int totalMoved = 0;
        foreach ((DateTimeOffset start, DateTimeOffset end) in chunks)
        {
            totalMoved += await _machineStateRepository.ReclassifyTelemetryForTenantAsync(
                tenantId, target, start, end, ct);
        }

        if (totalMoved > 0)
        {
            _logger.LogInformation(
                "Retention reclassify: moved {Count} telemetry row(s) for tenant {TenantId} into class {Class} ({RetentionDays}-day window)",
                totalMoved, tenantId, target, retentionDays);
        }
        else
        {
            _logger.LogDebug(
                "Retention reclassify: tenant {TenantId} already converged on class {Class}", tenantId, target);
        }
    }

    /// <summary>
    /// Enumerates the day-sized <c>ReceivedAt</c> ranges the reclassification covers, newest day
    /// first. The walk spans the tenant's new effective window — from <paramref name="now"/> minus the
    /// retention days through <paramref name="now"/> plus <see cref="FutureWindowDays"/> — split on UTC
    /// midnight so each range maps to exactly one daily leaf partition, with the two outer ranges
    /// clipped to the window edges. Anything older than the window is deliberately absent: those rows
    /// keep their old class. A non-positive retention clamps to the one-day Short window so a deny-all
    /// override or a resolution glitch cannot produce an inverted range.
    /// </summary>
    /// <param name="now">The current instant.</param>
    /// <param name="retentionDays">The tenant's current effective retention, in days.</param>
    /// <returns>Half-open [start, end) ranges, newest first.</returns>
    internal static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> BuildDayChunks(
        DateTimeOffset now, int retentionDays)
    {
        int effectiveDays = retentionDays > 0 ? retentionDays : RetentionClassPolicy.ShortWindowDays;
        DateTimeOffset windowStart = now.AddDays(-effectiveDays);
        DateTimeOffset windowEnd = now.AddDays(FutureWindowDays);

        List<(DateTimeOffset Start, DateTimeOffset End)> chunks = [];
        DateTimeOffset cursor = StartOfUtcDay(windowEnd);

        while (cursor.AddDays(1) > windowStart)
        {
            DateTimeOffset start = cursor > windowStart ? cursor : windowStart;
            DateTimeOffset end = cursor.AddDays(1) < windowEnd ? cursor.AddDays(1) : windowEnd;

            if (end > start)
            {
                chunks.Add((start, end));
            }

            cursor = cursor.AddDays(-1);
        }

        return chunks;
    }

    /// <summary>
    /// The UTC midnight that starts the day containing the given instant.
    /// </summary>
    private static DateTimeOffset StartOfUtcDay(DateTimeOffset instant)
    {
        DateTimeOffset utc = instant.ToUniversalTime();

        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
    }
}
