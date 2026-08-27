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
    public async Task ResolveForRun_JobAbsentFromStorage_ReportsMissing()
    {
        // Intent: the run path must not enqueue anything for a job it cannot resolve.
        InMemoryStorage storage = new();

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        RecurringJobRunTarget target = inspector.ResolveForRun(RecurringJobIds.TenantPurge);

        await Assert.That(target.Job).IsNull();
        await Assert.That(target.Status).IsEqualTo(RecurringJobHealth.Missing);
    }

    [Test]
    public async Task Inspect_UndeserialisablePayload_IsLoadFailed()
    {
        // Intent: after a type rename or namespace move a stored payload stops resolving. Hangfire
        // swallows that into LoadException and leaves Job null, so the job can never run again.
        // Reporting it as scheduled would show green on a job that is permanently broken.
        using InMemoryStorage storage = new();
        WriteRawJob(storage, RecurringJobIds.RemoteCommandExpiry, UnresolvableJobPayload);

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        RecurringJobSnapshot snapshot = Single(inspector, RecurringJobIds.RemoteCommandExpiry);

        await Assert.That(snapshot.Status).IsEqualTo(RecurringJobHealth.LoadFailed);
    }

    [Test]
    public async Task ResolveForRun_ReturnsTheJobMatchingTheRequestedId()
    {
        // Intent: the run path enqueues whatever this returns. If it ignored the id and returned
        // whichever job storage happened to yield first, "run tenant-purge" would silently enqueue
        // a different job and report success.
        using InMemoryStorage storage = new();
        WriteRecurringJob(storage, RecurringJobIds.PartitionManagement, "0 3 * * *", Now.UtcDateTime.AddHours(1));
        WriteRecurringJob(storage, RecurringJobIds.TenantPurge, "23 * * * *", Now.UtcDateTime.AddMinutes(10), jobType: typeof(OtherProbeJob));

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        RecurringJobRunTarget target = inspector.ResolveForRun(RecurringJobIds.TenantPurge);

        await Assert.That(target.Job).IsNotNull();
        await Assert.That(target.Job!.Type).IsEqualTo(typeof(OtherProbeJob));
    }

    [Test]
    public async Task ResolveForRun_UndeserialisablePayload_ReportsLoadFailed()
    {
        // Intent: the caller distinguishes "never registered" from "registered but unloadable" in
        // the message it shows the operator. Collapsing both to Missing sends them looking for a
        // registration problem when the real cause is a rename.
        using InMemoryStorage storage = new();
        WriteRawJob(storage, RecurringJobIds.TenantPurge, UnresolvableJobPayload);

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        RecurringJobRunTarget target = inspector.ResolveForRun(RecurringJobIds.TenantPurge);

        await Assert.That(target.Job).IsNull();
        await Assert.That(target.Status).IsEqualTo(RecurringJobHealth.LoadFailed);
    }

    [Test]
    public async Task ResolveForRun_RegistrationHashRemoved_ReportsMissing()
    {
        // Intent: the id is still in the set but its hash is gone, so Hangfire returns a stub with
        // no job definition. Enqueueing from that would dereference null; reporting Missing sends
        // the operator to the registration, which is where the problem actually is.
        using InMemoryStorage storage = new();

        using (IStorageConnection connection = storage.GetConnection())
        using (IWriteOnlyTransaction transaction = connection.CreateWriteTransaction())
        {
            transaction.AddToSet("recurring-jobs", RecurringJobIds.TenantPurge, 0);
            transaction.Commit();
        }

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        RecurringJobRunTarget target = inspector.ResolveForRun(RecurringJobIds.TenantPurge);

        await Assert.That(target.Job).IsNull();
        await Assert.That(target.Status).IsEqualTo(RecurringJobHealth.Missing);
    }

    [Test]
    public async Task Inspect_ErrorSetWithFutureNextExecution_IsSchedulingError()
    {
        // Intent: while Hangfire retries a failed scheduling pass it keeps NextExecution ~15s
        // ahead, so neither the overdue nor the disabled rule fires and the job would otherwise
        // render green for the whole ~75 seconds before it is disabled.
        using InMemoryStorage storage = new();
        WriteRecurringJob(
            storage,
            RecurringJobIds.AlertEvaluation,
            "* * * * *",
            Now.UtcDateTime.AddSeconds(15),
            error: "Recurring job can't be scheduled, see inner exception for details.",
            retryAttempt: 3);

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        RecurringJobSnapshot snapshot = Single(inspector, RecurringJobIds.AlertEvaluation);

        await Assert.That(snapshot.Status).IsEqualTo(RecurringJobHealth.SchedulingError);
    }

    [Test]
    public async Task Inspect_RetryAttemptSetButErrorCleared_IsScheduled()
    {
        // Intent: pins the choice of Error over RetryAttempt as the predicate. Hangfire's own
        // dashboard "Trigger now" clears Error but leaves RetryAttempt at its last value, and only
        // a successful scheduled pass resets the counter. Classifying on RetryAttempt would show a
        // scheduling failure on a healthy job until its next occurrence — most of a day for a
        // daily cron.
        using InMemoryStorage storage = new();
        WriteRecurringJob(
            storage,
            RecurringJobIds.PartitionManagement,
            "0 3 * * *",
            Now.UtcDateTime.AddHours(6),
            retryAttempt: 3);

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        RecurringJobSnapshot snapshot = Single(inspector, RecurringJobIds.PartitionManagement);

        await Assert.That(snapshot.Status).IsEqualTo(RecurringJobHealth.Scheduled);
    }

    [Test]
    public async Task Inspect_DisabledJobRetainingRetryState_IsDisabledNotSchedulingError()
    {
        // Intent: pins the branch order. Disable() leaves Error set and RetryAttempt at 5 forever,
        // so a disabled job matches the scheduling-error predicate too. Disabled is the more
        // severe and more accurate answer and must win.
        using InMemoryStorage storage = new();
        WriteRecurringJob(
            storage,
            RecurringJobIds.TenantPurge,
            "23 * * * *",
            nextExecution: null,
            error: "Recurring job can't be scheduled, see inner exception for details.",
            retryAttempt: 5);

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        RecurringJobSnapshot snapshot = Single(inspector, RecurringJobIds.TenantPurge);

        await Assert.That(snapshot.Status).IsEqualTo(RecurringJobHealth.Disabled);
    }

    [Test]
    public async Task Inspect_StaleNextExecutionWithError_IsOverdueNotSchedulingError()
    {
        // Intent: pins Overdue above the scheduling-error branch. If the worker dies mid-retry the
        // schedule goes stale; nothing is retrying because nothing is running, so "overdue" is the
        // truer and more actionable answer.
        using InMemoryStorage storage = new();
        WriteRecurringJob(
            storage,
            RecurringJobIds.HealthSweepCoordinator,
            "* * * * *",
            Now.UtcDateTime - RecurringJobInspector.OverdueGrace - TimeSpan.FromMinutes(5),
            error: "Recurring job can't be scheduled, see inner exception for details.",
            retryAttempt: 2);

        RecurringJobInspector inspector = new(storage, new FakeTimeProvider(Now));

        RecurringJobSnapshot snapshot = Single(inspector, RecurringJobIds.HealthSweepCoordinator);

        await Assert.That(snapshot.Status).IsEqualTo(RecurringJobHealth.Overdue);
    }

    private static RecurringJobSnapshot Single(RecurringJobInspector inspector, string id)
    {
        return inspector.Inspect().First(s => s.Id == id);
    }

    /// <summary>A well-formed payload naming a type that does not exist, so DeserializeJob throws.</summary>
    private const string UnresolvableJobPayload =
        "{\"Type\":\"Framlux.Does.Not.Exist, NoSuchAssembly\",\"Method\":\"RunAsync\",\"ParameterTypes\":\"[]\",\"Arguments\":\"[]\"}";

    private static void WriteRawJob(InMemoryStorage storage, string id, string jobPayload)
    {
        Dictionary<string, string> hash = new()
        {
            ["Cron"] = "* * * * *",
            ["Job"] = jobPayload,
            ["NextExecution"] = JobHelper.SerializeDateTime(Now.UtcDateTime.AddMinutes(10)),
            ["TimeZoneId"] = TimeZoneInfo.Utc.Id,
        };

        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.AddToSet("recurring-jobs", id, 0);
        transaction.SetRangeInHash($"recurring-job:{id}", hash);
        transaction.Commit();
    }

    private static void WriteRecurringJob(
        InMemoryStorage storage,
        string id,
        string cron,
        DateTime? nextExecution,
        string? error = null,
        Type? jobType = null,
        int retryAttempt = 0)
    {
        Dictionary<string, string> hash = new()
        {
            ["Cron"] = cron,
            ["Job"] = SerializeProbe(jobType ?? typeof(ProbeJob)),
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

        if (retryAttempt > 0)
        {
            hash["RetryAttempt"] = retryAttempt.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        using IStorageConnection connection = storage.GetConnection();
        using IWriteOnlyTransaction transaction = connection.CreateWriteTransaction();
        transaction.AddToSet("recurring-jobs", id, 0);
        transaction.SetRangeInHash($"recurring-job:{id}", hash);
        transaction.Commit();
    }

    private static string SerializeProbe(Type jobType)
    {
        if (jobType == typeof(OtherProbeJob))
        {
            return InvocationData.SerializeJob(
                Job.FromExpression<OtherProbeJob>(j => j.RunAsync(CancellationToken.None))).SerializePayload();
        }

        return InvocationData.SerializeJob(
            Job.FromExpression<ProbeJob>(j => j.RunAsync(CancellationToken.None))).SerializePayload();
    }

    /// <summary>A second stand-in type, so a test can prove the right job was resolved.</summary>
    public sealed class OtherProbeJob
    {
        /// <summary>Never invoked; the payload only has to deserialise.</summary>
        public Task RunAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
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
