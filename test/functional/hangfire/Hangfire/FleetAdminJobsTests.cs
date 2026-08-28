// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Hangfire;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.Vord.BillingGrpc;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Framlux.FleetManagement.FunctionalTest.Hangfire;

/// <summary>
/// Covers the fleet-admin recurring-job RPCs against a real Hangfire storage and processing
/// server, which is the only place the nine registrations and a live server row both exist.
/// </summary>
/// <remarks>
/// Runs serially, as does every other class in this project that builds a test host. Hangfire's DI
/// registration resolves the process-global JobStorage.Current, which each host's AddHangfire call
/// reassigns, so two hosts alive at once can read each other's storage. Worse, a concurrent
/// RegisterAll recomputes NextExecution for the very job whose schedule stability one of these
/// tests asserts. A keyless [NotInParallel] only serialises against other keyless ones, so the
/// attribute has to be on all of them, not just this class.
/// </remarks>
[NotInParallel]
public sealed class FleetAdminJobsTests
{
    [Test]
    public async Task ListRecurringJobs_AfterRegistration_ReturnsEveryKnownJob()
    {
        // Intent: the view is the operator's inventory of background work. Every id this build
        // knows about must come back, whether or not storage has it.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        ListRecurringJobsResponse response = await fixture.Service.ListRecurringJobs(
            new ListRecurringJobsRequest(), fixture.CallContext);

        await Assert.That(response.Jobs.Select(j => j.Id).OrderBy(id => id))
            .IsEquivalentTo(RecurringJobIds.All.OrderBy(id => id));
    }

    [Test]
    public async Task ListRecurringJobs_AfterRegistration_ReportsJobsAsScheduled()
    {
        // Intent: a freshly registered job must not read as Missing or Overdue, or the view
        // would alarm on a healthy platform.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        ListRecurringJobsResponse response = await fixture.Service.ListRecurringJobs(
            new ListRecurringJobsRequest(), fixture.CallContext);

        FleetRecurringJob job = response.Jobs.First(j => j.Id == RecurringJobIds.TenantPurge);

        await Assert.That(job.Status).IsEqualTo(FleetRecurringJobStatus.Scheduled);
    }

    [Test]
    public async Task ListRecurringJobs_ReturnsServerTime()
    {
        // Intent: the panel renders ages against the fleet's clock, not the browser's, so a wrong
        // value shifts every relative timestamp in the view. Asserting only non-null would pass
        // against any constant, so this brackets the call and requires the value to fall inside —
        // the bracket bounds the assertion, it does not define the pass condition.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        DateTimeOffset before = DateTimeOffset.UtcNow;

        ListRecurringJobsResponse response = await fixture.Service.ListRecurringJobs(
            new ListRecurringJobsRequest(), fixture.CallContext);

        DateTimeOffset after = DateTimeOffset.UtcNow;

        await Assert.That(response.ServerTime).IsNotNull();

        DateTimeOffset serverTime = response.ServerTime.ToDateTimeOffset();

        await Assert.That(serverTime).IsGreaterThanOrEqualTo(before);
        await Assert.That(serverTime).IsLessThanOrEqualTo(after);
    }

    [Test]
    public async Task ListRecurringJobs_WithProcessingServer_ReturnsServerBlock()
    {
        // Intent: worker heartbeat is the headline signal. If the server block is empty when a
        // worker is demonstrably running, the panel's most important indicator is broken.
        //
        // The server row is announced from a dedicated dispatcher thread that nothing in the test
        // factory awaits, so this polls rather than asserting immediately. This is not a
        // wall-clock assertion: the deadline only bounds failure, it never defines the pass
        // condition.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        ListRecurringJobsResponse response = new();
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            response = await fixture.Service.ListRecurringJobs(
                new ListRecurringJobsRequest(), fixture.CallContext);

            if (response.Workers.Count > 0)
            {
                break;
            }

            await Task.Delay(50);
        }

        await Assert.That(response.Workers.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task ListRecurringJobs_UnknownEnrichJobId_ReturnsRunMarkedNotRetained()
    {
        // Intent: a manual run whose job row Hangfire has expired must still appear, flagged as
        // no-longer-retained, rather than vanishing from the operator's history.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        ListRecurringJobsRequest request = new();
        request.EnrichJobIds.Add("nonexistent-job-id");

        ListRecurringJobsResponse response = await fixture.Service.ListRecurringJobs(
            request, fixture.CallContext);

        FleetManualRun run = response.ManualRuns.Single();

        await Assert.That(run.Retained).IsFalse();
    }

    [Test]
    public async Task RunRecurringJobNow_UnknownJobId_ThrowsInvalidArgument()
    {
        // Intent: this RPC must never become a generic enqueue-anything primitive. Only ids this
        // build declares may be run.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        RunRecurringJobNowRequest request = new() { JobId = "not-a-real-job", TriggeredBy = "ops@framlux.io" };

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await fixture.Service.RunRecurringJobNow(request, fixture.CallContext));

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
    }

    [Test]
    public async Task RunRecurringJobNow_JobAbsentFromStorage_ThrowsFailedPrecondition()
    {
        // Intent: a known id whose registration is missing has no job definition to enqueue.
        // Failing loudly beats enqueueing nothing and reporting success.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync(
            registerRecurringJobs: false);

        RunRecurringJobNowRequest request = new()
        {
            JobId = RecurringJobIds.TenantPurge,
            TriggeredBy = "ops@framlux.io",
        };

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await fixture.Service.RunRecurringJobNow(request, fixture.CallContext));

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
    }

    [Test]
    public async Task RunRecurringJobNow_KnownJob_ReturnsEnqueuedJobId()
    {
        // Intent: the caller records the enqueued id in its own audit store, which is what makes
        // a manual run traceable after Hangfire expires the job row.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        RunRecurringJobNowRequest request = new()
        {
            JobId = RecurringJobIds.TenantPurge,
            TriggeredBy = "ops@framlux.io",
        };

        RunRecurringJobNowResponse response = await fixture.Service.RunRecurringJobNow(
            request, fixture.CallContext);

        await Assert.That(response.Success).IsTrue();
        await Assert.That(response.EnqueuedJobId).IsNotEmpty();
    }

    [Test]
    public async Task RunRecurringJobNow_StampsRecurringJobIdAndTriggeredByParameters()
    {
        // Intent: the parameters are what link a manual run back to its recurring job and its
        // operator. Without them the run is an anonymous background job and the view cannot
        // attribute it.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        RunRecurringJobNowRequest request = new()
        {
            JobId = RecurringJobIds.TenantPurge,
            TriggeredBy = "ops@framlux.io",
        };

        RunRecurringJobNowResponse runResponse = await fixture.Service.RunRecurringJobNow(
            request, fixture.CallContext);

        ListRecurringJobsRequest listRequest = new();
        listRequest.EnrichJobIds.Add(runResponse.EnqueuedJobId);

        ListRecurringJobsResponse listResponse = await fixture.Service.ListRecurringJobs(
            listRequest, fixture.CallContext);

        FleetManualRun run = listResponse.ManualRuns.Single();

        // These assert the decoded values. Hangfire stores job parameters JSON-serialised, so the
        // raw stored value carries its quotes; the reader strips them.
        await Assert.That(run.RecurringJobId).IsEqualTo(RecurringJobIds.TenantPurge);
        await Assert.That(run.TriggeredBy).IsEqualTo("ops@framlux.io");
    }

    [Test]
    public async Task RunRecurringJobNow_EnqueuesTheJobTypeThatWasAskedFor()
    {
        // Intent: the operator picked a specific job. Resolving the wrong definition would enqueue
        // real work against the production fleet while reporting success — the run id and the
        // stamped parameters would all look correct, so nothing else in this suite would notice.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        RunRecurringJobNowRequest request = new()
        {
            JobId = RecurringJobIds.PartitionManagement,
            TriggeredBy = "ops@framlux.io",
        };

        RunRecurringJobNowResponse response = await fixture.Service.RunRecurringJobNow(
            request, fixture.CallContext);

        global::Hangfire.JobStorage storage =
            fixture.Factory.Services.GetRequiredService<global::Hangfire.JobStorage>();
        global::Hangfire.Storage.Monitoring.JobDetailsDto details =
            storage.GetMonitoringApi().JobDetails(response.EnqueuedJobId);

        await Assert.That(details.Job.Type).IsEqualTo(typeof(PartitionManagementJob));
    }

    [Test]
    public async Task RunRecurringJobNow_SecondRunWithinCooldown_ThrowsFailedPrecondition()
    {
        // Intent: every recurring job holds a DisableConcurrentExecution lock that blocks a worker
        // thread for up to its timeout — half an hour for the two long jobs — against ten workers
        // on one node. Without a cooldown, a few impatient clicks during an incident park most of
        // the pool and starve the per-minute jobs on the critical queue.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        RunRecurringJobNowRequest request = new()
        {
            JobId = RecurringJobIds.TenantPurge,
            TriggeredBy = "ops@framlux.io",
        };

        await fixture.Service.RunRecurringJobNow(request, fixture.CallContext);

        RpcException? exception = await Assert.ThrowsAsync<RpcException>(
            async () => await fixture.Service.RunRecurringJobNow(request, fixture.CallContext));

        await Assert.That(exception!.StatusCode).IsEqualTo(StatusCode.FailedPrecondition);
    }

    [Test]
    public async Task RunRecurringJobNow_LeavesRecurringScheduleUntouched()
    {
        // Intent: the regression guard for enqueueing rather than triggering. Hangfire's trigger
        // path rewrites LastExecution, clears Error and recomputes NextExecution from now,
        // discarding a pending overdue occurrence. If this handler were changed to trigger, a
        // manual run would erase the scheduled-run history the view exists to show.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        ListRecurringJobsResponse before = await fixture.Service.ListRecurringJobs(
            new ListRecurringJobsRequest(), fixture.CallContext);
        FleetRecurringJob beforeJob = before.Jobs.First(j => j.Id == RecurringJobIds.TenantPurge);

        RunRecurringJobNowRequest request = new()
        {
            JobId = RecurringJobIds.TenantPurge,
            TriggeredBy = "ops@framlux.io",
        };
        await fixture.Service.RunRecurringJobNow(request, fixture.CallContext);

        ListRecurringJobsResponse after = await fixture.Service.ListRecurringJobs(
            new ListRecurringJobsRequest(), fixture.CallContext);
        FleetRecurringJob afterJob = after.Jobs.First(j => j.Id == RecurringJobIds.TenantPurge);

        await Assert.That(afterJob.NextExecution).IsEqualTo(beforeJob.NextExecution);
        await Assert.That(afterJob.LastExecution).IsEqualTo(beforeJob.LastExecution);
    }

    [Test]
    public async Task ListRecurringJobs_WithAJobExecuting_ReportsItAsInFlight()
    {
        // Intent: the whole point of the in-flight list is telling "the queue is wedged" apart
        // from "something is running long", and only a real worker holding a real job proves the
        // list reports what is actually executing rather than what was merely enqueued.
        //
        // The probe parks the worker deliberately, so it is released in a finally: leaving it held
        // would stall the host's shutdown and take the rest of the class with it.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        using InFlightProbe probe = new();

        global::Hangfire.IBackgroundJobClient client =
            fixture.Factory.Services.GetRequiredService<global::Hangfire.IBackgroundJobClient>();
        string jobId = client.Create(
            global::Hangfire.Common.Job.FromExpression(() => InFlightProbe.Park()),
            new global::Hangfire.States.EnqueuedState());

        try
        {
            // The worker picks the job up on its own dispatcher thread that nothing here awaits,
            // so this polls. The deadline only bounds failure; it never defines the pass condition.
            FleetProcessingJob? running = null;
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(20);

            while (DateTimeOffset.UtcNow < deadline)
            {
                ListRecurringJobsResponse polled = await fixture.Service.ListRecurringJobs(
                    new ListRecurringJobsRequest(), fixture.CallContext);

                running = polled.ProcessingJobs.FirstOrDefault(j => j.JobId == jobId);

                if (running is not null)
                {
                    break;
                }

                await Task.Delay(50);
            }

            await Assert.That(running).IsNotNull();
            await Assert.That(running!.JobName).IsEqualTo("InFlightProbe.Park");
            await Assert.That(running.ServerId).IsNotEmpty();
            await Assert.That(running.StartedAt).IsNotNull();
            await Assert.That(running.InProcessingState).IsTrue();

            // An ordinary enqueue carries no RecurringJobId parameter, and that is a fact about
            // the job rather than a gap in the read — reporting Unknown here would tell an
            // operator the link might exist when it never can.
            await Assert.That(running.Attribution).IsEqualTo(FleetProcessingAttribution.NotRecurring);
        }
        finally
        {
            probe.Release();
        }
    }

    [Test]
    public async Task ListRecurringJobs_WithNothingExecuting_ReportsAnEmptyListNotAnUnknownSection()
    {
        // Intent: empty is the healthy steady state for in-flight work, so it must not be
        // reported as a storage fault. The inverse — a fault reported as empty — is what the
        // section name exists to prevent, and the two are indistinguishable without this.
        await using FleetAdminJobsFixture fixture = await FleetAdminJobsFixture.CreateAsync();

        ListRecurringJobsResponse response = await fixture.Service.ListRecurringJobs(
            new ListRecurringJobsRequest(), fixture.CallContext);

        await Assert.That(response.UnavailableSections).DoesNotContain("processing");
    }

    /// <summary>
    /// A job that parks its worker until released, so a background job is genuinely mid-execution
    /// while the RPC is called. Static because Hangfire has to serialise the invocation, and a
    /// static method needs no activation from a container it was never registered in.
    /// </summary>
    public sealed class InFlightProbe : IDisposable
    {
        private static readonly ManualResetEventSlim Gate = new(false);

        /// <summary>
        /// Creates a probe with the gate closed. The reset is what makes a second construction in
        /// the same process work at all: the gate has to be static for Hangfire to reach it from a
        /// serialised invocation, so without this a repeated or retried run would find it still
        /// set from the first, park nothing, and fail for a reason unrelated to the code under test.
        /// </summary>
        public InFlightProbe()
        {
            Gate.Reset();
        }

        /// <summary>Blocks the worker. The timeout bounds a leaked hold; it is not a delay.</summary>
        public static void Park()
        {
            Gate.Wait(TimeSpan.FromSeconds(30));
        }

        /// <summary>Lets the parked worker finish. Idempotent, so the finally and the dispose may both call it.</summary>
        public void Release()
        {
            Gate.Set();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Release();
        }
    }
}
