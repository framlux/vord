// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Hangfire;
using Hangfire;
using Hangfire.Common;
using Hangfire.InMemory;
using Hangfire.Storage;
using Microsoft.Extensions.Time.Testing;

namespace Framlux.FleetManagement.Test.Hangfire;

/// <summary>
/// Pins how recurring-job storage state maps to operator-facing health. Each case here
/// corresponds to a state Hangfire can genuinely produce; the Disabled and Removed cases in
/// particular would otherwise render as healthy and hide a stalled platform.
/// </summary>
public sealed class RecurringJobInspectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Inspect_JobWithFutureNextExecution_IsScheduled()
    {
        // Intent: the ordinary healthy case. A registered job whose next occurrence is ahead of
        // now must not be flagged, or every refresh would cry wolf.
        InMemoryStorage storage = new();
        WriteRecurringJob(storage, RecurringJobIds.TenantPurge, "23 * * * *", Now.UtcDateTime.AddMinutes(10));

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        RecurringJobSnapshot snapshot = Single(inspector, RecurringJobIds.TenantPurge);

        await Assert.That(snapshot.Status).IsEqualTo(RecurringJobHealth.Scheduled);
    }

    [Test]
    public async Task Inspect_NextExecutionOlderThanGrace_IsOverdue()
    {
        // Intent: a stopped worker leaves NextExecution in the past while the registration
        // survives. This is the signal that the schedule has stalled.
        InMemoryStorage storage = new();
        WriteRecurringJob(
            storage,
            RecurringJobIds.AlertEvaluation,
            "* * * * *",
            Now.UtcDateTime - RecurringJobInspector.OverdueGrace - TimeSpan.FromSeconds(1));

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        RecurringJobSnapshot snapshot = Single(inspector, RecurringJobIds.AlertEvaluation);

        await Assert.That(snapshot.Status).IsEqualTo(RecurringJobHealth.Overdue);
    }

    [Test]
    public async Task Inspect_NextExecutionWithinGrace_IsStillScheduled()
    {
        // Intent: the boundary. Per-minute jobs jitter around their occurrence; flagging them
        // one second late would make the view useless.
        InMemoryStorage storage = new();
        WriteRecurringJob(
            storage,
            RecurringJobIds.AlertEvaluation,
            "* * * * *",
            Now.UtcDateTime - RecurringJobInspector.OverdueGrace + TimeSpan.FromSeconds(1));

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        RecurringJobSnapshot snapshot = Single(inspector, RecurringJobIds.AlertEvaluation);

        await Assert.That(snapshot.Status).IsEqualTo(RecurringJobHealth.Scheduled);
    }

    [Test]
    public async Task Inspect_NullNextExecutionWithError_IsDisabledNotScheduled()
    {
        // Intent: THE regression test. Hangfire's RecurringJobEntity.Disable sets NextExecution
        // to null and Error to a message after five consecutive scheduling failures. A
        // "NextExecution < now - grace" rule is false against null, so a naive implementation
        // renders a permanently dead job as healthy.
        InMemoryStorage storage = new();
        WriteRecurringJob(
            storage,
            RecurringJobIds.PartitionManagement,
            "0 3 * * *",
            nextExecution: null,
            error: "Recurring job can't be scheduled, see inner exception for details.");

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        RecurringJobSnapshot snapshot = Single(inspector, RecurringJobIds.PartitionManagement);

        await Assert.That(snapshot.Status).IsEqualTo(RecurringJobHealth.Disabled);
    }

    [Test]
    public async Task Inspect_IdInSetButHashMissing_IsMissingNotScheduled()
    {
        // Intent: Hangfire returns { Id, Removed = true } with a null Cron when the hash is gone.
        // Treated naively that renders as Scheduled with a blank cron column.
        InMemoryStorage storage = new();

        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.AddToSet("recurring-jobs", RecurringJobIds.StripeSync, 0);
            transaction.Commit();
        }

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        RecurringJobSnapshot snapshot = Single(inspector, RecurringJobIds.StripeSync);

        await Assert.That(snapshot.Status).IsEqualTo(RecurringJobHealth.Missing);
    }

    [Test]
    public async Task Inspect_JobAbsentFromStorage_IsMissing()
    {
        // Intent: the worker never registered, or a deploy dropped a registration. Every id in
        // RecurringJobIds.All must appear in the result even when storage knows nothing about it.
        InMemoryStorage storage = new();

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        RecurringJobSnapshot snapshot = Single(inspector, RecurringJobIds.HealthSweepCoordinator);

        await Assert.That(snapshot.Status).IsEqualTo(RecurringJobHealth.Missing);
    }

    [Test]
    public async Task Inspect_ReturnsEveryKnownJobId()
    {
        // Intent: the view is the operator's inventory. A job that silently vanishes from the
        // result is exactly the failure this feature exists to surface.
        InMemoryStorage storage = new();

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        IReadOnlyList<RecurringJobSnapshot> snapshots = inspector.Inspect();

        await Assert.That(snapshots.Select(s => s.Id).OrderBy(id => id))
            .IsEquivalentTo(RecurringJobIds.All.OrderBy(id => id));
    }

    [Test]
    public async Task GetJobDefinition_UnknownId_ReturnsNull()
    {
        // Intent: the run path must not enqueue anything for a job it cannot resolve.
        InMemoryStorage storage = new();

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        Job? job = inspector.GetJobDefinition(RecurringJobIds.TenantPurge);

        await Assert.That(job).IsNull();
    }

    private static RecurringJobSnapshot Single(RecurringJobInspector inspector, string id)
    {
        return inspector.Inspect().First(s => s.Id == id);
    }

    private static void WriteRecurringJob(
        InMemoryStorage storage,
        string id,
        string cron,
        DateTime? nextExecution,
        string? error = null)
    {
        Dictionary<string, string> hash = new()
        {
            ["Cron"] = cron,
            ["Job"] = InvocationData.SerializeJob(
                Job.FromExpression<ProbeJob>(j => j.RunAsync(CancellationToken.None))).SerializePayload(),
            ["TimeZoneId"] = TimeZoneInfo.Utc.Id,
        };

        if (nextExecution.HasValue)
        {
            hash["NextExecution"] = JobHelper.SerializeDateTime(nextExecution.Value);
        }

        if (error is not null)
        {
            hash["Error"] = error;
        }

        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.AddToSet("recurring-jobs", id, 0);
        transaction.SetRangeInHash($"recurring-job:{id}", hash);
        transaction.Commit();
    }

    /// <summary>Stand-in job type used only to produce a serialisable payload for test fixtures.</summary>
    public sealed class ProbeJob
    {
        /// <summary>Never invoked; the payload only has to deserialise.</summary>
        public Task RunAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
