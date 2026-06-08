// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.DataExport;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Test.Infrastructure;
using Hangfire;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Framlux.FleetManagement.Test.Services;

public sealed class DataExportProcessingJobTests
{
    private static DataExportJob MakeJob(int id, DataExportJobStatus status = DataExportJobStatus.Pending, DateTimeOffset? startedAt = null)
    {
        return new DataExportJob
        {
            Id = id,
            TenantId = 1,
            Status = status,
            RequestedByUserId = 1,
            RequestedAt = DateTimeOffset.UtcNow,
            StartedAt = startedAt,
            ObjectKey = "",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            DownloadToken = Guid.NewGuid().ToString("N"),
        };
    }

    private static (IDataExportRepository Repo, IDataExportHandler Handler, ILogger<DataExportProcessingJob> Logger) BuildDeps()
    {
        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        // Defaults: no stuck rows, no pending rows, all claims succeed.
        repo.GetStuckProcessingJobsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new List<DataExportJob>());
        repo.GetPendingExportJobsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DataExportJob>());
        repo.TryClaimPendingJobAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);

        IDataExportHandler handler = Substitute.For<IDataExportHandler>();
        ILogger<DataExportProcessingJob> logger = Substitute.For<ILogger<DataExportProcessingJob>>();

        return (repo, handler, logger);
    }

    [Test]
    public async Task RunAsync_PendingJobs_ClaimsEachAndDelegatesToHandler()
    {
        // Intent: every pending row is processed exactly once per RunAsync, and each must go
        // through TryClaimPendingJobAsync (atomic Pending → Processing) before the handler runs.
        // This is the contract that prevents two workers from double-processing the same row.
        (IDataExportRepository repo, IDataExportHandler handler, ILogger<DataExportProcessingJob> logger) = BuildDeps();

        repo.GetPendingExportJobsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DataExportJob> { MakeJob(11), MakeJob(22), MakeJob(33) });

        DataExportProcessingJob job = new(repo, handler, logger);

        await job.RunAsync(CancellationToken.None);

        await repo.Received(1).TryClaimPendingJobAsync(11, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await repo.Received(1).TryClaimPendingJobAsync(22, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await repo.Received(1).TryClaimPendingJobAsync(33, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await handler.Received(1).ProcessExportJobAsync(11, Arg.Any<CancellationToken>());
        await handler.Received(1).ProcessExportJobAsync(22, Arg.Any<CancellationToken>());
        await handler.Received(1).ProcessExportJobAsync(33, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_NoPendingJobs_HandlerNeverCalled()
    {
        // Intent: the dominant runtime path. Recurring tick with an empty queue must not call
        // the handler and must not flap the claim path.
        (IDataExportRepository repo, IDataExportHandler handler, ILogger<DataExportProcessingJob> logger) = BuildDeps();

        DataExportProcessingJob job = new(repo, handler, logger);

        await job.RunAsync(CancellationToken.None);

        await handler.DidNotReceive().ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().TryClaimPendingJobAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_OrphanProcessingJob_IsResetToPendingBeforeProcessing()
    {
        // Intent: rows stuck in Processing past the threshold (worker crashed mid-export) must be
        // reset to Pending so the next pass picks them up. Without this the row sits in Processing
        // forever and the user's export never completes.
        (IDataExportRepository repo, IDataExportHandler handler, ILogger<DataExportProcessingJob> logger) = BuildDeps();

        DataExportJob orphan = MakeJob(77, DataExportJobStatus.Processing, DateTimeOffset.UtcNow.AddHours(-2));
        repo.GetStuckProcessingJobsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new List<DataExportJob> { orphan });

        DataExportProcessingJob job = new(repo, handler, logger);

        await job.RunAsync(CancellationToken.None);

        await repo.Received(1).ResetOrphanedJobToPendingAsync(77, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessSingleAsync_ClaimSucceeds_HandlerInvokedExactlyOnce()
    {
        // Intent: the per-job path enqueued by the data-export create endpoint. When the row is
        // still Pending, claim it and process. Single delegation, no fleet sweep.
        (IDataExportRepository repo, IDataExportHandler handler, ILogger<DataExportProcessingJob> logger) = BuildDeps();

        DataExportProcessingJob job = new(repo, handler, logger);

        await job.ProcessSingleAsync(jobId: 42, CancellationToken.None);

        await repo.Received(1).TryClaimPendingJobAsync(42, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await handler.Received(1).ProcessExportJobAsync(42, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessSingleAsync_ClaimFails_HandlerNotInvoked()
    {
        // Intent: if another worker already claimed the row (or it was never Pending), the handler
        // must NOT be invoked. Two enqueued copies of the same job id cannot both run the export.
        (IDataExportRepository repo, IDataExportHandler handler, ILogger<DataExportProcessingJob> logger) = BuildDeps();

        repo.TryClaimPendingJobAsync(99, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(false);

        DataExportProcessingJob job = new(repo, handler, logger);

        await job.ProcessSingleAsync(jobId: 99, CancellationToken.None);

        await handler.DidNotReceive().ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessSingleAsync_HandlerThrows_ResetsRowToPendingAndRethrows()
    {
        // Intent: a transport-level failure that escapes the handler should leave the row in
        // Pending (not Processing) so the next tick / on-demand enqueue can retry it cleanly.
        // The exception is rethrown so Hangfire records the run as failed in its dashboard.
        (IDataExportRepository repo, IDataExportHandler handler, ILogger<DataExportProcessingJob> logger) = BuildDeps();

        handler.ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("S3 down"));

        DataExportProcessingJob job = new(repo, handler, logger);

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.ProcessSingleAsync(jobId: 5, CancellationToken.None));
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.Message).IsEqualTo("S3 down");
        await repo.Received(1).ResetOrphanedJobToPendingAsync(5, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessSingleAsync_NonPositiveJobId_Throws()
    {
        // Intent: the per-job endpoint should refuse obviously-bogus ids fast, before touching DB.
        (IDataExportRepository repo, IDataExportHandler handler, ILogger<DataExportProcessingJob> logger) = BuildDeps();

        DataExportProcessingJob job = new(repo, handler, logger);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => job.ProcessSingleAsync(0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => job.ProcessSingleAsync(-1, CancellationToken.None));
        await repo.DidNotReceive().TryClaimPendingJobAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_RepositoryThrows_ExceptionPropagatesToHangfire()
    {
        // Intent: failures while listing pending jobs must propagate. No defensive swallow — let
        // Hangfire mark the run failed and surface in the dashboard.
        (IDataExportRepository repo, IDataExportHandler handler, ILogger<DataExportProcessingJob> logger) = BuildDeps();

        repo.GetPendingExportJobsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<List<DataExportJob>>>(_ => throw new InvalidOperationException("DB down"));

        DataExportProcessingJob job = new(repo, handler, logger);

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.RunAsync(CancellationToken.None));
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.Message).IsEqualTo("DB down");
        await handler.DidNotReceive().ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_CancellationRequested_StopsProcessingRemainingJobs()
    {
        // Intent: cancellation between per-job calls must short-circuit further work so a worker
        // shutdown does not begin a fresh export it cannot finish.
        using CancellationTokenSource cts = new();

        (IDataExportRepository repo, IDataExportHandler handler, ILogger<DataExportProcessingJob> logger) = BuildDeps();

        repo.GetPendingExportJobsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DataExportJob> { MakeJob(11), MakeJob(22), MakeJob(33) });

        int handledCount = 0;
        handler.ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                handledCount++;
                cts.Cancel();

                return Task.CompletedTask;
            });

        DataExportProcessingJob job = new(repo, handler, logger);

        await job.RunAsync(cts.Token);

        await Assert.That(handledCount).IsEqualTo(1);
    }

    [Test]
    public async Task RunAsync_TokenIsForwardedToRepositoryAndHandler()
    {
        // Intent: the CancellationToken must flow into every async call so long-running uploads
        // and DB queries can short-circuit on worker shutdown.
        using CancellationTokenSource cts = new();

        (IDataExportRepository repo, IDataExportHandler handler, ILogger<DataExportProcessingJob> logger) = BuildDeps();

        repo.GetPendingExportJobsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DataExportJob> { MakeJob(11) });

        DataExportProcessingJob job = new(repo, handler, logger);

        await job.RunAsync(cts.Token);

        await repo.Received(1).GetPendingExportJobsAsync(cts.Token);
        await repo.Received(1).GetStuckProcessingJobsAsync(Arg.Any<DateTimeOffset>(), cts.Token);
        await repo.Received(1).TryClaimPendingJobAsync(11, Arg.Any<DateTimeOffset>(), cts.Token);
        await handler.Received(1).ProcessExportJobAsync(11, cts.Token);
    }

    [Test]
    public async Task Constructor_NullRepository_Throws()
    {
        IDataExportHandler handler = Substitute.For<IDataExportHandler>();
        ILogger<DataExportProcessingJob> logger = Substitute.For<ILogger<DataExportProcessingJob>>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            DataExportProcessingJob _ = new(null!, handler, logger);

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("dataExportRepository");
    }

    [Test]
    public async Task Constructor_NullHandler_Throws()
    {
        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        ILogger<DataExportProcessingJob> logger = Substitute.For<ILogger<DataExportProcessingJob>>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            DataExportProcessingJob _ = new(repo, null!, logger);

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("dataExportHandler");
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        IDataExportHandler handler = Substitute.For<IDataExportHandler>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            DataExportProcessingJob _ = new(repo, handler, null!);

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    [Test]
    public async Task ProcessSingleAsync_DoesNotCarryDisableConcurrentExecution()
    {
        // Intent: ProcessSingleAsync is enqueued per-row from the API endpoint. A method-scoped
        // DisableConcurrentExecution would serialize every export across the fleet through one
        // global lock. The atomic TryClaimPendingJobAsync handles per-row idempotency already.
        MethodInfo method = typeof(DataExportProcessingJob)
            .GetMethod(nameof(DataExportProcessingJob.ProcessSingleAsync))
            ?? throw new InvalidOperationException("ProcessSingleAsync not found");
        DisableConcurrentExecutionAttribute? attr = method.GetCustomAttribute<DisableConcurrentExecutionAttribute>();

        await Assert.That(attr).IsNull();
    }

    [Test]
    public async Task ProcessSingleAsync_AutomaticRetryAttribute_IsZeroAttempts()
    {
        // Intent: a failed ProcessSingleAsync resets the row to Pending; the next recurring
        // RunAsync sweep picks it up. Hangfire retries would compete with that recovery path.
        MethodInfo method = typeof(DataExportProcessingJob)
            .GetMethod(nameof(DataExportProcessingJob.ProcessSingleAsync))
            ?? throw new InvalidOperationException("ProcessSingleAsync not found");
        AutomaticRetryAttribute? attr = method.GetCustomAttribute<AutomaticRetryAttribute>();

        await Assert.That(attr).IsNotNull();
        await Assert.That(attr!.Attempts).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_AutomaticRetryAttribute_IsZeroAttempts()
    {
        // Intent: pin Hangfire AutomaticRetry to 0 attempts. The default of 10 would cause
        // duplicate executions on transient failure; this job is not idempotent under retry.
        MethodInfo method = typeof(DataExportProcessingJob).GetMethod(nameof(DataExportProcessingJob.RunAsync))
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
        MethodInfo method = typeof(DataExportProcessingJob).GetMethod(nameof(DataExportProcessingJob.RunAsync))
            ?? throw new InvalidOperationException("RunAsync not found");
        CustomAttributeData? attrData = method.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(DisableConcurrentExecutionAttribute));

        await Assert.That(attrData).IsNotNull();
        await Assert.That(attrData!.ConstructorArguments.Count).IsEqualTo(1);
        int timeoutSeconds = (int)attrData.ConstructorArguments[0].Value!;
        await Assert.That(timeoutSeconds).IsEqualTo(1800);
    }

    [Test]
    public async Task ProcessSingleAsync_ResetThrows_OriginalExceptionPropagates()
    {
        // Intent: when the handler throws, the job tries to reset the row to Pending in its catch
        // block. If THAT reset call also throws, the operator must still see the ORIGINAL handler
        // failure — masking it behind a "reset failed" message would hide the real root cause.
        // Verify the original handler exception is what surfaces (either directly or as the inner
        // exception of an AggregateException).
        (IDataExportRepository repo, IDataExportHandler handler, ILogger<DataExportProcessingJob> logger) = BuildDeps();

        InvalidOperationException original = new("S3 upload timed out");
        handler.ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(original);
        repo.ResetOrphanedJobToPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB unreachable during reset"));

        DataExportProcessingJob job = new(repo, handler, logger);

        Exception? caught = await Assert.ThrowsAsync<Exception>(
            () => job.ProcessSingleAsync(jobId: 5, CancellationToken.None));
        await Assert.That(caught).IsNotNull();

        // Accept either the original surfaced directly OR an AggregateException whose inner is
        // the original. Anything else (e.g. only the reset failure surfaces) is a regression.
        bool isOriginal = ReferenceEquals(caught, original) || (caught!.Message == original.Message);
        bool isAggregateWithOriginalInner = (caught is AggregateException agg)
            && agg.InnerExceptions.Any(e => ReferenceEquals(e, original) || (e.Message == original.Message));

        await Assert.That(isOriginal || isAggregateWithOriginalInner).IsTrue();
        await repo.Received(1).ResetOrphanedJobToPendingAsync(5, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_CancellationDuringStuckSweep_StopsBeforeProcessingLoop()
    {
        // Intent: a shutdown that fires during the orphan-reset phase must prevent the pending-
        // job processing loop from starting. Otherwise a worker that's being torn down would
        // claim a Pending row it cannot finish, leaving it stuck in Processing for the next pass.
        using CancellationTokenSource cts = new();

        (IDataExportRepository repo, IDataExportHandler handler, ILogger<DataExportProcessingJob> logger) = BuildDeps();

        // Return a stuck row so the reset phase has something to do; cancel inside the reset call.
        repo.GetStuckProcessingJobsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new List<DataExportJob>
            {
                MakeJob(101, DataExportJobStatus.Processing, DateTimeOffset.UtcNow.AddHours(-2)),
            });
        repo.ResetOrphanedJobToPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();

                return Task.CompletedTask;
            });
        // Pre-stage pending jobs that MUST NOT be touched if cancellation is honored.
        repo.GetPendingExportJobsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DataExportJob> { MakeJob(11), MakeJob(22) });

        DataExportProcessingJob job = new(repo, handler, logger);

        await job.RunAsync(cts.Token);

        // The stuck-reset call happened (and was the trigger for cancellation).
        await repo.Received(1).ResetOrphanedJobToPendingAsync(101, Arg.Any<CancellationToken>());
        // The pending-job processing loop never started: no claim attempts and no handler invocations.
        await repo.DidNotReceive().TryClaimPendingJobAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await handler.DidNotReceive().ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ConcurrentClaim_OnlyOneSucceeds()
    {
        // Intent: pin the contract that TryClaimPendingJobAsync is atomic — only one of N
        // concurrent ProcessSingleAsync calls for the same job id may succeed in claiming it,
        // and the handler must be invoked exactly once across all callers.
        //
        // NOTE: SQLite serializes writes per-connection, so this test does not actually
        // reproduce the multi-process race in the unit-test environment. Instead it pins the
        // observable contract — exactly one handler invocation for N parallel callers — which
        // would fail loudly if the claim were ever changed to be non-atomic (e.g. a read-then-
        // write replacement of the conditional UPDATE).
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        DataExportJob seed = new()
        {
            TenantId = 1, Status = DataExportJobStatus.Pending, RequestedByUserId = 1,
            RequestedAt = DateTimeOffset.UtcNow, ObjectKey = "",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            DownloadToken = Guid.NewGuid().ToString("N"),
        };
        seed.Id = await db.InsertWithInt32IdentityAsync(seed);

        ILogger<DatabaseRepository> repoLogger = Substitute.For<ILogger<DatabaseRepository>>();
        DatabaseRepository repository = new(db, repoLogger);
        IDataExportHandler handler = Substitute.For<IDataExportHandler>();
        // Handler completes the row so a subsequent claim attempt (had the claim not been atomic)
        // would still observe Status != Pending.
        handler.ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await repository.CompleteExportJobAsync(seed.Id, "exports/done.zip", 1024, CancellationToken.None);
            });
        ILogger<DataExportProcessingJob> logger = Substitute.For<ILogger<DataExportProcessingJob>>();

        DataExportProcessingJob job = new(repository, handler, logger);

        Task[] callers = new Task[]
        {
            job.ProcessSingleAsync(seed.Id, CancellationToken.None),
            job.ProcessSingleAsync(seed.Id, CancellationToken.None),
            job.ProcessSingleAsync(seed.Id, CancellationToken.None),
            job.ProcessSingleAsync(seed.Id, CancellationToken.None),
        };
        await Task.WhenAll(callers);

        // Exactly one handler invocation across the four concurrent callers.
        await handler.Received(1).ProcessExportJobAsync(seed.Id, Arg.Any<CancellationToken>());

        // And the row finished as Complete (handler ran exactly once and persisted Complete).
        DataExportJob? after = await db.DataExportJobs.FirstOrDefaultAsync(j => j.Id == seed.Id);
        await Assert.That(after).IsNotNull();
        await Assert.That(after!.Status).IsEqualTo(DataExportJobStatus.Complete);
    }

    // ==========================================================================================
    // H7 regression: poison-job retry cap. FailureCount increments on each failure; after
    // MaxFailureAttempts the row transitions to Failed instead of being re-claimed forever.
    // ==========================================================================================

    [Test]
    public async Task ProcessSingleAsync_HandlerThrows_IncrementsFailureCount()
    {
        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        repo.TryClaimPendingJobAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        repo.IncrementFailureCountAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(1);
        IDataExportHandler handler = Substitute.For<IDataExportHandler>();
        handler.ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("transport failure"));
        DataExportProcessingJob job = new(repo, handler, Substitute.For<ILogger<DataExportProcessingJob>>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => job.ProcessSingleAsync(42, CancellationToken.None));

        await repo.Received(1).IncrementFailureCountAsync(42, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessSingleAsync_UnderRetryBudget_ResetsToPending()
    {
        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        repo.TryClaimPendingJobAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        repo.IncrementFailureCountAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(2);
        IDataExportHandler handler = Substitute.For<IDataExportHandler>();
        handler.ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("transient"));
        DataExportProcessingJob job = new(repo, handler, Substitute.For<ILogger<DataExportProcessingJob>>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => job.ProcessSingleAsync(99, CancellationToken.None));

        await repo.Received(1).ResetOrphanedJobToPendingAsync(99, Arg.Any<CancellationToken>());
        await repo.DidNotReceive().MarkExportJobFailedAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProcessSingleAsync_ExhaustedBudget_MarksFailed_DoesNotReset()
    {
        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        repo.TryClaimPendingJobAsync(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(true);
        repo.IncrementFailureCountAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(DataExportProcessingJob.MaxFailureAttempts);
        IDataExportHandler handler = Substitute.For<IDataExportHandler>();
        handler.ProcessExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("permanent"));
        DataExportProcessingJob job = new(repo, handler, Substitute.For<ILogger<DataExportProcessingJob>>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => job.ProcessSingleAsync(7, CancellationToken.None));

        await repo.Received(1).MarkExportJobFailedAsync(7, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await repo.DidNotReceive().ResetOrphanedJobToPendingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

}
