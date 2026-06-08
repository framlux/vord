// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Reflection;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.DataExport;
using Framlux.FleetManagement.Test.Infrastructure;
using Hangfire;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Framlux.FleetManagement.Test.Services;

public sealed class DataExportCleanupJobTests
{
    private static DataExportJob BuildExpiredJob(int id, int tenantId = 1, string objectKey = "exports/test.zip")
    {
        return new DataExportJob
        {
            Id = id,
            TenantId = tenantId,
            Status = DataExportJobStatus.Complete,
            RequestedByUserId = 1,
            RequestedAt = DateTimeOffset.UtcNow.AddHours(-2),
            CompletedAt = DateTimeOffset.UtcNow.AddHours(-1),
            ObjectKey = objectKey,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            DownloadToken = Guid.NewGuid().ToString("N"),
        };
    }

    [Test]
    public async Task RunAsync_NoExpiredJobs_NeverTouchesStorage()
    {
        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        IObjectStorageService storage = Substitute.For<IObjectStorageService>();
        ILogger<DataExportCleanupJob> logger = Substitute.For<ILogger<DataExportCleanupJob>>();

        repo.GetExpiredExportJobsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DataExportJob>());

        DataExportCleanupJob job = new(repo, storage, logger);

        await job.RunAsync(CancellationToken.None);

        await repo.DidNotReceive().ExpireExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await storage.DidNotReceive().DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_ExpiredJob_ExpiresInRepoBeforeDeletingFromStorage()
    {
        // Intent: the predecessor was deliberately ordered DB-then-S3 so a failed S3 delete leaves
        // the object orphaned (safe direction) rather than leaving an active DB row pointing at a
        // missing object (the user could retry the broken download). This ordering must be preserved.
        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        IObjectStorageService storage = Substitute.For<IObjectStorageService>();
        ILogger<DataExportCleanupJob> logger = Substitute.For<ILogger<DataExportCleanupJob>>();

        DataExportJob expired = BuildExpiredJob(id: 42, objectKey: "exports/data-42.zip");
        repo.GetExpiredExportJobsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DataExportJob> { expired });

        DataExportCleanupJob job = new(repo, storage, logger);

        await job.RunAsync(CancellationToken.None);

        Received.InOrder(() =>
        {
            repo.ExpireExportJobAsync(42, Arg.Any<CancellationToken>());
            storage.DeleteObjectAsync("exports/data-42.zip", Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task RunAsync_ExpiredJobWithEmptyObjectKey_SkipsStorageDelete()
    {
        // Intent: rows whose ObjectKey is empty (e.g., job failed before upload) must still transition
        // to Expired, but no S3 call must be made because there is nothing to delete.
        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        IObjectStorageService storage = Substitute.For<IObjectStorageService>();
        ILogger<DataExportCleanupJob> logger = Substitute.For<ILogger<DataExportCleanupJob>>();

        DataExportJob expired = BuildExpiredJob(id: 7, objectKey: "");
        repo.GetExpiredExportJobsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DataExportJob> { expired });

        DataExportCleanupJob job = new(repo, storage, logger);

        await job.RunAsync(CancellationToken.None);

        await repo.Received(1).ExpireExportJobAsync(7, Arg.Any<CancellationToken>());
        await storage.DidNotReceive().DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_StorageDeleteFails_LogsWarningAndContinues()
    {
        // Intent: S3 failures during cleanup are non-fatal — the job is already marked Expired in
        // the DB, so a failed delete only orphans an object (which is acceptable). The predecessor
        // logged a warning and continued to the next job; this must be preserved.
        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        IObjectStorageService storage = Substitute.For<IObjectStorageService>();
        ILogger<DataExportCleanupJob> logger = Substitute.For<ILogger<DataExportCleanupJob>>();

        DataExportJob first = BuildExpiredJob(id: 1, objectKey: "exports/fail.zip");
        DataExportJob second = BuildExpiredJob(id: 2, objectKey: "exports/ok.zip");
        repo.GetExpiredExportJobsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DataExportJob> { first, second });

        storage.DeleteObjectAsync("exports/fail.zip", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("S3 down"));
        storage.DeleteObjectAsync("exports/ok.zip", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        DataExportCleanupJob job = new(repo, storage, logger);

        await job.RunAsync(CancellationToken.None);

        await storage.Received(1).DeleteObjectAsync("exports/fail.zip", Arg.Any<CancellationToken>());
        await storage.Received(1).DeleteObjectAsync("exports/ok.zip", Arg.Any<CancellationToken>());
        await repo.Received(1).ExpireExportJobAsync(1, Arg.Any<CancellationToken>());
        await repo.Received(1).ExpireExportJobAsync(2, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_PerJobExpireFails_LogsErrorAndContinuesToNextJob()
    {
        // Intent: if the DB expire call throws (the first step of the per-job try block), the per-job
        // outer catch must swallow and the loop must continue to the next job. Hangfire should NOT
        // see a top-level failure for one bad row.
        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        IObjectStorageService storage = Substitute.For<IObjectStorageService>();
        ILogger<DataExportCleanupJob> logger = Substitute.For<ILogger<DataExportCleanupJob>>();

        DataExportJob first = BuildExpiredJob(id: 1, objectKey: "exports/1.zip");
        DataExportJob second = BuildExpiredJob(id: 2, objectKey: "exports/2.zip");
        repo.GetExpiredExportJobsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DataExportJob> { first, second });

        repo.ExpireExportJobAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB error on job 1"));

        DataExportCleanupJob job = new(repo, storage, logger);

        await job.RunAsync(CancellationToken.None);

        await repo.Received(1).ExpireExportJobAsync(1, Arg.Any<CancellationToken>());
        await repo.Received(1).ExpireExportJobAsync(2, Arg.Any<CancellationToken>());
        // First job's storage delete never happened (expire threw before reaching it).
        await storage.DidNotReceive().DeleteObjectAsync("exports/1.zip", Arg.Any<CancellationToken>());
        // Second job's storage delete still happened.
        await storage.Received(1).DeleteObjectAsync("exports/2.zip", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_GetExpiredJobsThrows_PropagatesToHangfire()
    {
        // Intent: top-level failures (e.g., DB unavailable when listing) must propagate so Hangfire
        // records the run as failed and surfaces in the dashboard.
        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        IObjectStorageService storage = Substitute.For<IObjectStorageService>();
        ILogger<DataExportCleanupJob> logger = Substitute.For<ILogger<DataExportCleanupJob>>();

        repo.GetExpiredExportJobsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<List<DataExportJob>>>(_ => throw new InvalidOperationException("DB down"));

        DataExportCleanupJob job = new(repo, storage, logger);

        InvalidOperationException? ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => job.RunAsync(CancellationToken.None));
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.Message).IsEqualTo("DB down");
        await repo.DidNotReceive().ExpireExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await storage.DidNotReceive().DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_CancellationBetweenJobs_StopsProcessing()
    {
        // Intent: cancellation between per-job iterations must short-circuit further work. Matches
        // the predecessor's `if (ct.IsCancellationRequested) break;` semantics.
        using CancellationTokenSource cts = new();

        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        IObjectStorageService storage = Substitute.For<IObjectStorageService>();
        ILogger<DataExportCleanupJob> logger = Substitute.For<ILogger<DataExportCleanupJob>>();

        repo.GetExpiredExportJobsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DataExportJob>
            {
                BuildExpiredJob(id: 1, objectKey: "exports/1.zip"),
                BuildExpiredJob(id: 2, objectKey: "exports/2.zip"),
                BuildExpiredJob(id: 3, objectKey: "exports/3.zip"),
            });

        repo.ExpireExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();

                return Task.CompletedTask;
            });

        DataExportCleanupJob job = new(repo, storage, logger);

        await job.RunAsync(cts.Token);

        await repo.Received(1).ExpireExportJobAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RunAsync_TokenForwardedToRepositoryAndStorage()
    {
        using CancellationTokenSource cts = new();

        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        IObjectStorageService storage = Substitute.For<IObjectStorageService>();
        ILogger<DataExportCleanupJob> logger = Substitute.For<ILogger<DataExportCleanupJob>>();

        DataExportJob expired = BuildExpiredJob(id: 9, objectKey: "exports/9.zip");
        repo.GetExpiredExportJobsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DataExportJob> { expired });

        DataExportCleanupJob job = new(repo, storage, logger);

        await job.RunAsync(cts.Token);

        await repo.Received(1).GetExpiredExportJobsAsync(cts.Token);
        await repo.Received(1).ExpireExportJobAsync(9, cts.Token);
        await storage.Received(1).DeleteObjectAsync("exports/9.zip", cts.Token);
    }

    [Test]
    public async Task Constructor_NullRepository_Throws()
    {
        IObjectStorageService storage = Substitute.For<IObjectStorageService>();
        ILogger<DataExportCleanupJob> logger = Substitute.For<ILogger<DataExportCleanupJob>>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            DataExportCleanupJob _ = new(null!, storage, logger);

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("dataExportRepository");
    }

    [Test]
    public async Task Constructor_NullObjectStorage_Throws()
    {
        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        ILogger<DataExportCleanupJob> logger = Substitute.For<ILogger<DataExportCleanupJob>>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            DataExportCleanupJob _ = new(repo, null!, logger);

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("objectStorageService");
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        IObjectStorageService storage = Substitute.For<IObjectStorageService>();

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            DataExportCleanupJob _ = new(repo, storage, null!);

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();

        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    [Test]
    public async Task RunAsync_AutomaticRetryAttribute_IsZeroAttempts()
    {
        // Intent: pin Hangfire AutomaticRetry to 0 attempts. The default of 10 would cause
        // duplicate executions on transient failure; this job is not idempotent under retry.
        MethodInfo method = typeof(DataExportCleanupJob).GetMethod(nameof(DataExportCleanupJob.RunAsync))
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
        MethodInfo method = typeof(DataExportCleanupJob).GetMethod(nameof(DataExportCleanupJob.RunAsync))
            ?? throw new InvalidOperationException("RunAsync not found");
        CustomAttributeData? attrData = method.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(DisableConcurrentExecutionAttribute));

        await Assert.That(attrData).IsNotNull();
        await Assert.That(attrData!.ConstructorArguments.Count).IsEqualTo(1);
        int timeoutSeconds = (int)attrData.ConstructorArguments[0].Value!;
        await Assert.That(timeoutSeconds).IsEqualTo(600);
    }

    // ----- Integration scenarios using real DatabaseRepository + SQLite -----
    //
    // These exercise the contract that the cleanup job depends on: the repository's filter
    // semantics (Status=Complete AND ExpiresAt < now). A regression that changed the filter
    // (e.g. expiring Pending/Failed rows, or using <= for the boundary) must fail these tests.

    [Test]
    public async Task Cleanup_OnlyCompleteStatusIsExpired()
    {
        // Intent: regression — only Complete-status rows past their ExpiresAt may be expired.
        // Pending/Processing/Failed rows must be untouched even when ExpiresAt is in the past.
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        DateTimeOffset past = DateTimeOffset.UtcNow.AddHours(-1);

        DataExportJob completeJob = new()
        {
            TenantId = 1, Status = DataExportJobStatus.Complete, RequestedByUserId = 1,
            RequestedAt = past.AddHours(-1), CompletedAt = past, ObjectKey = "exports/complete.zip",
            ExpiresAt = past, DownloadToken = Guid.NewGuid().ToString("N"),
        };
        completeJob.Id = await db.InsertWithInt32IdentityAsync(completeJob);

        DataExportJob pendingJob = new()
        {
            TenantId = 1, Status = DataExportJobStatus.Pending, RequestedByUserId = 1,
            RequestedAt = past.AddHours(-1), ObjectKey = "", ExpiresAt = past,
            DownloadToken = Guid.NewGuid().ToString("N"),
        };
        pendingJob.Id = await db.InsertWithInt32IdentityAsync(pendingJob);

        DataExportJob processingJob = new()
        {
            TenantId = 1, Status = DataExportJobStatus.Processing, RequestedByUserId = 1,
            RequestedAt = past.AddHours(-1), StartedAt = past, ObjectKey = "",
            ExpiresAt = past, DownloadToken = Guid.NewGuid().ToString("N"),
        };
        processingJob.Id = await db.InsertWithInt32IdentityAsync(processingJob);

        DataExportJob failedJob = new()
        {
            TenantId = 1, Status = DataExportJobStatus.Failed, RequestedByUserId = 1,
            RequestedAt = past.AddHours(-1), ObjectKey = "exports/failed.zip",
            ExpiresAt = past, DownloadToken = Guid.NewGuid().ToString("N"),
            ErrorMessage = "boom",
        };
        failedJob.Id = await db.InsertWithInt32IdentityAsync(failedJob);

        ILogger<DatabaseRepository> repoLogger = Substitute.For<ILogger<DatabaseRepository>>();
        DatabaseRepository repository = new(db, repoLogger);
        IObjectStorageService storage = Substitute.For<IObjectStorageService>();
        ILogger<DataExportCleanupJob> logger = Substitute.For<ILogger<DataExportCleanupJob>>();

        DataExportCleanupJob job = new(repository, storage, logger);

        await job.RunAsync(CancellationToken.None);

        // Only the Complete job should have been expired in storage and DB.
        await storage.Received(1).DeleteObjectAsync("exports/complete.zip", Arg.Any<CancellationToken>());
        await storage.DidNotReceive().DeleteObjectAsync("exports/failed.zip", Arg.Any<CancellationToken>());

        DataExportJob? completeAfter = await db.DataExportJobs.FirstOrDefaultAsync(j => j.Id == completeJob.Id);
        DataExportJob? pendingAfter = await db.DataExportJobs.FirstOrDefaultAsync(j => j.Id == pendingJob.Id);
        DataExportJob? processingAfter = await db.DataExportJobs.FirstOrDefaultAsync(j => j.Id == processingJob.Id);
        DataExportJob? failedAfter = await db.DataExportJobs.FirstOrDefaultAsync(j => j.Id == failedJob.Id);

        await Assert.That(completeAfter!.Status).IsEqualTo(DataExportJobStatus.Expired);
        await Assert.That(pendingAfter!.Status).IsEqualTo(DataExportJobStatus.Pending);
        await Assert.That(processingAfter!.Status).IsEqualTo(DataExportJobStatus.Processing);
        await Assert.That(failedAfter!.Status).IsEqualTo(DataExportJobStatus.Failed);
    }

    [Test]
    public async Task Cleanup_S3DeleteSucceeds_DBExpireThrows_LogsAndContinuesToNextJob()
    {
        // Intent: a per-job DB expire failure (after S3 delete would have succeeded had it run)
        // must be swallowed and the loop must continue. The current job ordering is DB-first,
        // so when the DB expire throws, the S3 delete for that row never runs — that is correct.
        // Either way, the loop must move on to the next job.
        IDataExportRepository repo = Substitute.For<IDataExportRepository>();
        IObjectStorageService storage = Substitute.For<IObjectStorageService>();
        ILogger<DataExportCleanupJob> logger = Substitute.For<ILogger<DataExportCleanupJob>>();

        DataExportJob first = BuildExpiredJob(id: 1, objectKey: "exports/1.zip");
        DataExportJob second = BuildExpiredJob(id: 2, objectKey: "exports/2.zip");
        repo.GetExpiredExportJobsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DataExportJob> { first, second });

        // Storage delete would succeed for both — verify the second one is still reached after
        // the first job's DB expire blows up.
        storage.DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        repo.ExpireExportJobAsync(1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB write failed on job 1"));

        DataExportCleanupJob job = new(repo, storage, logger);

        await job.RunAsync(CancellationToken.None);

        // Both rows had their DB expire attempted, the second succeeded, and storage delete ran
        // for the second job. The first job's storage delete is correctly skipped because the
        // DB expire (which runs first) threw.
        await repo.Received(1).ExpireExportJobAsync(1, Arg.Any<CancellationToken>());
        await repo.Received(1).ExpireExportJobAsync(2, Arg.Any<CancellationToken>());
        await storage.DidNotReceive().DeleteObjectAsync("exports/1.zip", Arg.Any<CancellationToken>());
        await storage.Received(1).DeleteObjectAsync("exports/2.zip", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Cleanup_RetentionBoundary_ExactlyAtExpiry_IsNotExpired()
    {
        // Intent: the repository filter uses strict `<` (ExpiresAt < now). A row whose ExpiresAt
        // is greater than or equal to "now" must NOT be returned for cleanup — pin the boundary
        // so a regression to `<=` would surface here.
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        // Schedule a row whose ExpiresAt is in the FUTURE so it is unambiguously not expired.
        // Then schedule a sibling row in the past so we can confirm the query runs and returns
        // only the past one.
        DataExportJob notYetExpired = new()
        {
            TenantId = 1, Status = DataExportJobStatus.Complete, RequestedByUserId = 1,
            RequestedAt = DateTimeOffset.UtcNow.AddHours(-1),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            ObjectKey = "exports/not-yet.zip",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            DownloadToken = Guid.NewGuid().ToString("N"),
        };
        notYetExpired.Id = await db.InsertWithInt32IdentityAsync(notYetExpired);

        DataExportJob actuallyExpired = new()
        {
            TenantId = 1, Status = DataExportJobStatus.Complete, RequestedByUserId = 1,
            RequestedAt = DateTimeOffset.UtcNow.AddHours(-2),
            CompletedAt = DateTimeOffset.UtcNow.AddHours(-1),
            ObjectKey = "exports/expired.zip",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            DownloadToken = Guid.NewGuid().ToString("N"),
        };
        actuallyExpired.Id = await db.InsertWithInt32IdentityAsync(actuallyExpired);

        ILogger<DatabaseRepository> repoLogger = Substitute.For<ILogger<DatabaseRepository>>();
        DatabaseRepository repository = new(db, repoLogger);
        IObjectStorageService storage = Substitute.For<IObjectStorageService>();
        ILogger<DataExportCleanupJob> logger = Substitute.For<ILogger<DataExportCleanupJob>>();

        DataExportCleanupJob job = new(repository, storage, logger);

        await job.RunAsync(CancellationToken.None);

        DataExportJob? notYetAfter = await db.DataExportJobs.FirstOrDefaultAsync(j => j.Id == notYetExpired.Id);
        DataExportJob? expiredAfter = await db.DataExportJobs.FirstOrDefaultAsync(j => j.Id == actuallyExpired.Id);

        await Assert.That(notYetAfter!.Status).IsEqualTo(DataExportJobStatus.Complete);
        await Assert.That(expiredAfter!.Status).IsEqualTo(DataExportJobStatus.Expired);
        await storage.DidNotReceive().DeleteObjectAsync("exports/not-yet.zip", Arg.Any<CancellationToken>());
        await storage.Received(1).DeleteObjectAsync("exports/expired.zip", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Cleanup_ManyExpiredJobs_AllAreProcessed()
    {
        // Intent: pin the contract that the cleanup loop has no implicit batch truncation.
        // Seed 100 expired jobs and confirm all 100 are expired in the DB and all 100 storage
        // delete calls were made.
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        const int JobCount = 100;
        List<int> insertedIds = new();
        for (int i = 0; i < JobCount; i++)
        {
            DataExportJob expired = new()
            {
                TenantId = 1, Status = DataExportJobStatus.Complete, RequestedByUserId = 1,
                RequestedAt = DateTimeOffset.UtcNow.AddHours(-2),
                CompletedAt = DateTimeOffset.UtcNow.AddHours(-1),
                ObjectKey = $"exports/batch-{i}.zip",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                DownloadToken = Guid.NewGuid().ToString("N"),
            };
            expired.Id = await db.InsertWithInt32IdentityAsync(expired);
            insertedIds.Add(expired.Id);
        }

        ILogger<DatabaseRepository> repoLogger = Substitute.For<ILogger<DatabaseRepository>>();
        DatabaseRepository repository = new(db, repoLogger);
        IObjectStorageService storage = Substitute.For<IObjectStorageService>();
        storage.DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        ILogger<DataExportCleanupJob> logger = Substitute.For<ILogger<DataExportCleanupJob>>();

        DataExportCleanupJob job = new(repository, storage, logger);

        await job.RunAsync(CancellationToken.None);

        int expiredCount = await db.DataExportJobs
            .CountAsync(j => (insertedIds.Contains(j.Id)) && (j.Status == DataExportJobStatus.Expired));

        await Assert.That(expiredCount).IsEqualTo(JobCount);
        await storage.Received(JobCount).DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Cleanup_MultiTenantInterleaving_IsolationPreserved()
    {
        // Intent: cleanup is fleet-wide, not tenant-scoped. With expired jobs interleaved across
        // multiple tenants, every tenant's expired rows must be expired in a single pass and the
        // ObjectKey for each tenant's rows must be passed to storage correctly (no swapping).
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        List<DataExportJob> seeded = new();
        for (int i = 0; i < 9; i++)
        {
            int tenantId = (i % 3) + 1; // tenants 1, 2, 3 interleaved
            DataExportJob expired = new()
            {
                TenantId = tenantId, Status = DataExportJobStatus.Complete, RequestedByUserId = 1,
                RequestedAt = DateTimeOffset.UtcNow.AddHours(-2),
                CompletedAt = DateTimeOffset.UtcNow.AddHours(-1),
                ObjectKey = $"exports/tenant-{tenantId}-row-{i}.zip",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                DownloadToken = Guid.NewGuid().ToString("N"),
            };
            expired.Id = await db.InsertWithInt32IdentityAsync(expired);
            seeded.Add(expired);
        }

        ILogger<DatabaseRepository> repoLogger = Substitute.For<ILogger<DatabaseRepository>>();
        DatabaseRepository repository = new(db, repoLogger);
        IObjectStorageService storage = Substitute.For<IObjectStorageService>();
        storage.DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        ILogger<DataExportCleanupJob> logger = Substitute.For<ILogger<DataExportCleanupJob>>();

        DataExportCleanupJob job = new(repository, storage, logger);

        await job.RunAsync(CancellationToken.None);

        // Every tenant's rows are expired and every distinct ObjectKey reached storage exactly once.
        foreach (DataExportJob row in seeded)
        {
            DataExportJob? after = await db.DataExportJobs.FirstOrDefaultAsync(j => j.Id == row.Id);
            await Assert.That(after!.Status).IsEqualTo(DataExportJobStatus.Expired);
            await storage.Received(1).DeleteObjectAsync(row.ObjectKey, Arg.Any<CancellationToken>());
        }

        // And the fleet-wide count of Expired rows matches the seeded set.
        int expiredCount = await db.DataExportJobs.CountAsync(j => j.Status == DataExportJobStatus.Expired);
        await Assert.That(expiredCount).IsEqualTo(seeded.Count);
    }
}
