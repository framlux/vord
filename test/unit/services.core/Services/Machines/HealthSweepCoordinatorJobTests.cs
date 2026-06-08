// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Linq.Expressions;
using System.Reflection;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Machines;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Framlux.FleetManagement.Test.Services.Machines;

public sealed class HealthSweepCoordinatorJobTests
{
    [Test]
    public async Task RunAsync_ActiveTenants_EnqueuesOnePerTenantJob()
    {
        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();
        repo.GetDistinctTenantIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<int> { 7, 42, 99 });

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        ILogger<HealthSweepCoordinatorJob> logger = Substitute.For<ILogger<HealthSweepCoordinatorJob>>();

        HealthSweepCoordinatorJob job = new(repo, backgroundJobClient, logger);

        await job.RunAsync(CancellationToken.None);

        backgroundJobClient.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(HealthSweepTenantJob) && (int)j.Args[0]! == 7),
            Arg.Any<EnqueuedState>());
        backgroundJobClient.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(HealthSweepTenantJob) && (int)j.Args[0]! == 42),
            Arg.Any<EnqueuedState>());
        backgroundJobClient.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(HealthSweepTenantJob) && (int)j.Args[0]! == 99),
            Arg.Any<EnqueuedState>());
    }

    [Test]
    public async Task RunAsync_NoActiveTenants_NeverEnqueuesTenantJobs()
    {
        // Intent: on an empty cluster (no machines registered yet), the coordinator must not
        // enqueue any per-tenant work. The secondary +30s self-schedule still occurs (verified
        // separately) — this test specifically asserts the tenant-job enqueue path is silent.
        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();
        repo.GetDistinctTenantIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<int>());

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        HealthSweepCoordinatorJob job = new(repo, backgroundJobClient, Substitute.For<ILogger<HealthSweepCoordinatorJob>>());

        await job.RunAsync(CancellationToken.None);

        backgroundJobClient.DidNotReceive().Create(
            Arg.Is<Job>(j => j.Type == typeof(HealthSweepTenantJob)),
            Arg.Any<IState>());
    }

    [Test]
    public async Task RunAsync_RepositoryThrows_PropagatesToHangfire()
    {
        // Intent: an infrastructure failure listing tenants must surface so Hangfire records a
        // failed run; the recurring tick will retry.
        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();
        repo.GetDistinctTenantIdsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<List<int>>>(_ => throw new InvalidOperationException("DB down"));

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        HealthSweepCoordinatorJob job = new(repo, backgroundJobClient, Substitute.For<ILogger<HealthSweepCoordinatorJob>>());

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.RunAsync(CancellationToken.None));
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.Message).IsEqualTo("DB down");
        // Per H6 the +30 s secondary fan-out is scheduled BEFORE the immediate fan-out, so the
        // repository failure does NOT prevent the secondary schedule from being recorded — but
        // the per-tenant enqueues never happen.
        backgroundJobClient.DidNotReceive().Create(
            Arg.Is<Job>(j => j.Type == typeof(HealthSweepTenantJob)),
            Arg.Any<IState>());
    }

    [Test]
    public async Task RunAsync_EnqueueThrowsForOneTenant_RemainingTenantsStillEnqueued()
    {
        // Intent: per-tenant enqueue failures must not stop the coordinator from enqueueing the
        // rest. One bad tenant id shouldn't take down the entire fleet's health sweep.
        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();
        repo.GetDistinctTenantIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<int> { 1, 2, 3 });

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        int callCount = 0;
        backgroundJobClient.Create(
            Arg.Is<Job>(j => j.Type == typeof(HealthSweepTenantJob)),
            Arg.Any<IState>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new InvalidOperationException("Hangfire transient error");
                }

                return "job-id";
            });

        HealthSweepCoordinatorJob job = new(repo, backgroundJobClient, Substitute.For<ILogger<HealthSweepCoordinatorJob>>());

        await job.RunAsync(CancellationToken.None);

        // All three tenants were enqueued (the failure on tenant 1 did not block the rest).
        backgroundJobClient.Received(3).Create(
            Arg.Is<Job>(j => j.Type == typeof(HealthSweepTenantJob)),
            Arg.Any<IState>());
    }

    [Test]
    public async Task RunAsync_TokenForwardedToRepository()
    {
        using CancellationTokenSource cts = new();

        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();
        repo.GetDistinctTenantIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<int>());

        HealthSweepCoordinatorJob job = new(
            repo,
            Substitute.For<IBackgroundJobClient>(),
            Substitute.For<ILogger<HealthSweepCoordinatorJob>>());

        await job.RunAsync(cts.Token);

        await repo.Received(1).GetDistinctTenantIdsAsync(cts.Token);
    }

    [Test]
    public async Task Constructor_NullRepository_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            HealthSweepCoordinatorJob _ = new(
                null!,
                Substitute.For<IBackgroundJobClient>(),
                Substitute.For<ILogger<HealthSweepCoordinatorJob>>());

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("machineStateRepository");
    }

    [Test]
    public async Task Constructor_NullBackgroundJobClient_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            HealthSweepCoordinatorJob _ = new(
                Substitute.For<IMachineStateRepository>(),
                null!,
                Substitute.For<ILogger<HealthSweepCoordinatorJob>>());

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("backgroundJobClient");
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            HealthSweepCoordinatorJob _ = new(
                Substitute.For<IMachineStateRepository>(),
                Substitute.For<IBackgroundJobClient>(),
                null!);

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    [Test]
    public async Task RunAsync_AlsoSchedulesSecondaryFanOutAtThirtySeconds()
    {
        // Intent: Hangfire's cron is minute-granularity but the predecessor service swept every
        // 15 seconds. The coordinator restores sub-minute cadence by scheduling a follow-up
        // FanOutAsync at +30s within each recurring tick. Without this scheduling call the
        // effective sweep cadence regresses to 60s, delaying machine-offline alerts.
        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();
        repo.GetDistinctTenantIdsAsync(Arg.Any<CancellationToken>()).Returns(new List<int>());

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        ILogger<HealthSweepCoordinatorJob> logger = Substitute.For<ILogger<HealthSweepCoordinatorJob>>();

        HealthSweepCoordinatorJob job = new(repo, backgroundJobClient, logger);

        await job.RunAsync(CancellationToken.None);

        // The scheduled call must target FanOutAsync on HealthSweepCoordinatorJob and use a
        // ~30-second delay (ScheduledState carries the absolute target time, which we cannot
        // pin exactly because UtcNow shifts under us — instead assert the method name).
        backgroundJobClient.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(HealthSweepCoordinatorJob)
                          && j.Method.Name == nameof(HealthSweepCoordinatorJob.FanOutAsync)),
            Arg.Any<ScheduledState>());
    }

    [Test]
    public async Task FanOutAsync_AutomaticRetryAttribute_IsZeroAttempts()
    {
        // Intent: FanOutAsync runs as a separately-scheduled Hangfire job. Default Hangfire
        // retry is 10 attempts; that would cause hours of duplicate tenant-job enqueues on
        // transient failure. Pin Attempts == 0 so the next coordinator tick is the only retry path.
        MethodInfo method = typeof(HealthSweepCoordinatorJob)
            .GetMethod(nameof(HealthSweepCoordinatorJob.FanOutAsync))
            ?? throw new InvalidOperationException("FanOutAsync not found");
        AutomaticRetryAttribute? attr = method.GetCustomAttribute<AutomaticRetryAttribute>();

        await Assert.That(attr).IsNotNull();
        await Assert.That(attr!.Attempts).IsEqualTo(0);
    }

    [Test]
    public async Task FanOutAsync_DisableConcurrentExecution_TimeoutMatchesRunAsync()
    {
        // Intent: a +30s scheduled FanOutAsync must not run concurrently with the next minute's
        // RunAsync. Use the same 30s timeout as RunAsync to keep semantics symmetric.
        MethodInfo method = typeof(HealthSweepCoordinatorJob)
            .GetMethod(nameof(HealthSweepCoordinatorJob.FanOutAsync))
            ?? throw new InvalidOperationException("FanOutAsync not found");
        CustomAttributeData? attrData = method.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(DisableConcurrentExecutionAttribute));

        await Assert.That(attrData).IsNotNull();
        await Assert.That(attrData!.ConstructorArguments.Count).IsEqualTo(1);
        int timeoutSeconds = (int)attrData.ConstructorArguments[0].Value!;
        await Assert.That(timeoutSeconds).IsEqualTo(30);
    }

    [Test]
    public async Task FanOutAsync_EnqueuesPerTenantJobsWithoutRescheduling()
    {
        // Intent: the +30s scheduled fan-out invokes FanOutAsync directly. It must NOT also
        // schedule another follow-up — otherwise we'd get a runaway chain of scheduled jobs
        // doubling every 30 seconds.
        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();
        repo.GetDistinctTenantIdsAsync(Arg.Any<CancellationToken>()).Returns(new List<int> { 1 });

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        ILogger<HealthSweepCoordinatorJob> logger = Substitute.For<ILogger<HealthSweepCoordinatorJob>>();

        HealthSweepCoordinatorJob job = new(repo, backgroundJobClient, logger);

        await job.FanOutAsync(CancellationToken.None);

        // Exactly one enqueue (for the single tenant) — no Schedule call.
        backgroundJobClient.Received(1).Create(Arg.Any<Job>(), Arg.Any<EnqueuedState>());
        backgroundJobClient.DidNotReceive().Create(Arg.Any<Job>(), Arg.Any<ScheduledState>());
    }

    [Test]
    public async Task RunAsync_AutomaticRetryAttribute_IsZeroAttempts()
    {
        // Intent: pin Hangfire AutomaticRetry to 0 attempts. The default of 10 would cause
        // duplicate executions on transient failure; this job is not idempotent under retry.
        MethodInfo method = typeof(HealthSweepCoordinatorJob).GetMethod(nameof(HealthSweepCoordinatorJob.RunAsync))
            ?? throw new InvalidOperationException("RunAsync not found");
        AutomaticRetryAttribute? attr = method.GetCustomAttribute<AutomaticRetryAttribute>();

        await Assert.That(attr).IsNotNull();
        await Assert.That(attr!.Attempts).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_DisableConcurrentExecution_TimeoutMatchesContract()
    {
        // Intent: pin the lock timeout. Use CustomAttributeData since DisableConcurrentExecutionAttribute
        // does not expose timeout via a public property.
        MethodInfo method = typeof(HealthSweepCoordinatorJob).GetMethod(nameof(HealthSweepCoordinatorJob.RunAsync))
            ?? throw new InvalidOperationException("RunAsync not found");
        CustomAttributeData? attrData = method.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(DisableConcurrentExecutionAttribute));

        await Assert.That(attrData).IsNotNull();
        await Assert.That(attrData!.ConstructorArguments.Count).IsEqualTo(1);
        int timeoutSeconds = (int)attrData.ConstructorArguments[0].Value!;
        await Assert.That(timeoutSeconds).IsEqualTo(30);
    }

    // ==========================================================================================
    // H6 regression tests: secondary fan-out is scheduled BEFORE the immediate fan-out, and
    // a transient schedule failure does not poison the primary fan-out.
    // ==========================================================================================

    [Test]
    public async Task RunAsync_FanOutThrows_SecondaryStillScheduled()
    {
        // The secondary fan-out at +30s must be enqueued even when the immediate fan-out fails.
        // Otherwise a one-shot DB hiccup downgrades sweep cadence from 30 s to 60 s for that
        // minute — silently.
        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();
        repo.GetDistinctTenantIdsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<List<int>>>(_ => throw new InvalidOperationException("DB down"));

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        HealthSweepCoordinatorJob job = new(repo, backgroundJobClient, Substitute.For<ILogger<HealthSweepCoordinatorJob>>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => job.RunAsync(CancellationToken.None));

        // The +30 s scheduled fan-out IS a Hangfire job that calls Create under the hood with
        // a Schedule state. We verify a Create on the coordinator type was attempted.
        backgroundJobClient.Received(1).Create(
            Arg.Is<Job>(j => j.Type == typeof(HealthSweepCoordinatorJob)),
            Arg.Any<IState>());
    }

    [Test]
    public async Task RunAsync_ScheduleThrows_PrimaryFanOutStillRuns()
    {
        // A failure scheduling the secondary fan-out must NOT prevent the immediate fan-out
        // from enqueuing per-tenant jobs.
        IMachineStateRepository repo = Substitute.For<IMachineStateRepository>();
        repo.GetDistinctTenantIdsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<int> { 1, 2 });

        IBackgroundJobClient backgroundJobClient = Substitute.For<IBackgroundJobClient>();
        // Throw for the coordinator schedule but allow the per-tenant enqueues.
        backgroundJobClient.Create(
            Arg.Is<Job>(j => j.Type == typeof(HealthSweepCoordinatorJob)),
            Arg.Any<IState>())
            .Returns(_ => throw new InvalidOperationException("Hangfire storage offline"));
        backgroundJobClient.Create(
            Arg.Is<Job>(j => j.Type == typeof(HealthSweepTenantJob)),
            Arg.Any<IState>())
            .Returns("ok");

        HealthSweepCoordinatorJob job = new(repo, backgroundJobClient, Substitute.For<ILogger<HealthSweepCoordinatorJob>>());

        // RunAsync must not throw.
        await job.RunAsync(CancellationToken.None);

        // Per-tenant jobs were still enqueued.
        backgroundJobClient.Received(2).Create(
            Arg.Is<Job>(j => j.Type == typeof(HealthSweepTenantJob)),
            Arg.Any<IState>());
    }
}
