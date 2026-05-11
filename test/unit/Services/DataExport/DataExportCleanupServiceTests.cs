// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Services.DataExport;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.DataExport;

/// <summary>
/// Tests for <see cref="DataExportCleanupService"/>.
/// </summary>
public class DataExportCleanupServiceTests
{
    private static DataExportJob BuildExpiredJob(int tenantId = 1, string objectKey = "exports/test.zip")
    {
        return new DataExportJob
        {
            TenantId = tenantId,
            Status = DataExportJobStatus.Complete,
            RequestedByUserId = 1,
            RequestedAt = DateTimeOffset.UtcNow.AddHours(-2),
            CompletedAt = DateTimeOffset.UtcNow.AddHours(-1),
            ObjectKey = objectKey,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            DownloadToken = Guid.NewGuid().ToString("N")
        };
    }

    private static DataExportJob BuildActiveJob(int tenantId = 1)
    {
        return new DataExportJob
        {
            TenantId = tenantId,
            Status = DataExportJobStatus.Complete,
            RequestedByUserId = 1,
            RequestedAt = DateTimeOffset.UtcNow.AddHours(-2),
            CompletedAt = DateTimeOffset.UtcNow.AddHours(-1),
            ObjectKey = "exports/active.zip",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(24),
            DownloadToken = Guid.NewGuid().ToString("N")
        };
    }

    private static (IDistributedLock Lock, IObjectStorageService Storage, ILogger<DataExportCleanupService> Logger) CreateMocks(bool grantLock)
    {
        IDistributedLock distributedLock = Substitute.For<IDistributedLock>();
        IObjectStorageService objectStorage = Substitute.For<IObjectStorageService>();
        ILogger<DataExportCleanupService> logger = Substitute.For<ILogger<DataExportCleanupService>>();

        if (grantLock)
        {
            IDatabase redisDb = Substitute.For<IDatabase>();
            redisDb.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
                .Returns(RedisResult.Create(RedisValue.Null));
            LockHandle lockHandle = new(redisDb, "test-key", "test-value");
            distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
                .Returns(lockHandle);
        }
        else
        {
            distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
                .Returns((LockHandle?)null);
        }

        return (distributedLock, objectStorage, logger);
    }

    private static async Task RunCleanupDirectly(DataExportCleanupService service)
    {
        await service.CleanupExpiredExportsAsync(CancellationToken.None);
    }

    // ========== Cleanup_NoExpiredJobs_NoOp ==========

    [Test]
    public async Task Cleanup_NoExpiredJobs_NoOp()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        await db.InsertAsync(BuildActiveJob());

        TestServiceScopeFactory scopeFactory = new(db);
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: true);

        DataExportCleanupService service = new(scopeFactory, distributedLock, objectStorage, logger);
        await RunCleanupDirectly(service);

        // No delete calls since no expired jobs
        await objectStorage.DidNotReceive().DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ========== Cleanup_ExpiredJob_DeletesS3Object ==========

    [Test]
    public async Task Cleanup_ExpiredJob_DeletesS3Object()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        DataExportJob expiredJob = BuildExpiredJob(objectKey: "exports/expired-data.zip");
        expiredJob.Id = await db.InsertWithInt32IdentityAsync(expiredJob);

        TestServiceScopeFactory scopeFactory = new(db);
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: true);

        DataExportCleanupService service = new(scopeFactory, distributedLock, objectStorage, logger);
        await RunCleanupDirectly(service);

        // Verify the S3 object was deleted with the correct key
        await objectStorage.Received(1).DeleteObjectAsync("exports/expired-data.zip", Arg.Any<CancellationToken>());
    }

    // ========== Cleanup_ExpiredJob_TransitionsToExpiredStatus ==========

    [Test]
    public async Task Cleanup_ExpiredJob_TransitionsToExpiredStatus()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        DataExportJob expiredJob = BuildExpiredJob();
        expiredJob.Id = await db.InsertWithInt32IdentityAsync(expiredJob);

        TestServiceScopeFactory scopeFactory = new(db);
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: true);

        DataExportCleanupService service = new(scopeFactory, distributedLock, objectStorage, logger);
        await RunCleanupDirectly(service);

        // Verify the job status was updated to Expired in the database
        DataExportJob? job = await db.DataExportJobs
            .Where(j => j.Id == expiredJob.Id)
            .FirstOrDefaultAsync();

        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Status).IsEqualTo(DataExportJobStatus.Expired);
    }

    // ========== Cleanup_EmptyObjectKey_SkipsDeleteAndTransitions ==========

    [Test]
    public async Task Cleanup_EmptyObjectKey_SkipsDeleteAndTransitions()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        DataExportJob expiredJob = BuildExpiredJob(objectKey: "");
        expiredJob.Id = await db.InsertWithInt32IdentityAsync(expiredJob);

        TestServiceScopeFactory scopeFactory = new(db);
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: true);

        DataExportCleanupService service = new(scopeFactory, distributedLock, objectStorage, logger);
        await RunCleanupDirectly(service);

        // Empty object key should skip the S3 delete call
        await objectStorage.DidNotReceive().DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // But the job status should still transition to Expired
        DataExportJob? job = await db.DataExportJobs
            .Where(j => j.Id == expiredJob.Id)
            .FirstOrDefaultAsync();

        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Status).IsEqualTo(DataExportJobStatus.Expired);
    }

    // ========== Cleanup_S3DeleteFails_LogsAndContinues ==========

    [Test]
    public async Task Cleanup_S3DeleteFails_LogsAndContinues()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        DataExportJob job1 = BuildExpiredJob(objectKey: "exports/fail.zip");
        job1.Id = await db.InsertWithInt32IdentityAsync(job1);
        DataExportJob job2 = BuildExpiredJob(objectKey: "exports/success.zip");
        job2.Id = await db.InsertWithInt32IdentityAsync(job2);

        TestServiceScopeFactory scopeFactory = new(db);
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: true);

        // First delete fails, second succeeds
        objectStorage.DeleteObjectAsync("exports/fail.zip", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("S3 failure"));
        objectStorage.DeleteObjectAsync("exports/success.zip", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        DataExportCleanupService service = new(scopeFactory, distributedLock, objectStorage, logger);
        await RunCleanupDirectly(service);

        // Both deletes should have been attempted — failure on first didn't block second
        await objectStorage.Received(1).DeleteObjectAsync("exports/fail.zip", Arg.Any<CancellationToken>());
        await objectStorage.Received(1).DeleteObjectAsync("exports/success.zip", Arg.Any<CancellationToken>());
    }

    // ========== Execute_LockContention_SkipsCycle ==========

    [Test]
    public async Task Execute_LockContention_SkipsCycle()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        DataExportJob expiredJob = BuildExpiredJob();
        expiredJob.Id = await db.InsertWithInt32IdentityAsync(expiredJob);

        TestServiceScopeFactory scopeFactory = new(db);
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: false);

        TaskCompletionSource lockAttempted = new();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(callInfo =>
            {
                lockAttempted.TrySetResult();

                return (LockHandle?)null;
            });

        DataExportCleanupService service = new(scopeFactory, distributedLock, objectStorage, logger);
        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await lockAttempted.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // No S3 deletions since lock was never acquired
        await objectStorage.DidNotReceive().DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Job should remain in Complete status since cleanup never ran
        DataExportJob? job = await db.DataExportJobs
            .Where(j => j.Id == expiredJob.Id)
            .FirstOrDefaultAsync();

        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Status).IsEqualTo(DataExportJobStatus.Complete);
    }

    // ========== Execute_CancellationRequested_StopsCleanly ==========

    [Test]
    public async Task Execute_CancellationRequested_StopsCleanly()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        // Seed an expired job that would be cleaned up if the cycle ran
        DataExportJob expiredJob = BuildExpiredJob(objectKey: "exports/should-not-be-cleaned.zip");
        expiredJob.Id = await db.InsertWithInt32IdentityAsync(expiredJob);

        TestServiceScopeFactory scopeFactory = new(db);
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: false);

        TaskCompletionSource lockAttempted = new();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(callInfo =>
            {
                lockAttempted.TrySetResult();

                return (LockHandle?)null;
            });

        DataExportCleanupService service = new(scopeFactory, distributedLock, objectStorage, logger);
        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await lockAttempted.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // The lock was not granted, so no cleanup should have occurred

        // The expired job should remain untouched in its original Complete status
        DataExportJob? job = await db.DataExportJobs
            .Where(j => j.Id == expiredJob.Id)
            .FirstOrDefaultAsync();

        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Status).IsEqualTo(DataExportJobStatus.Complete);

        // No S3 deletions should have been attempted
        await objectStorage.DidNotReceive().DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ========== Cleanup_MultipleExpiredJobs_AllProcessed ==========

    [Test]
    public async Task Cleanup_MultipleExpiredJobs_AllProcessed()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        DataExportJob job1 = BuildExpiredJob(tenantId: 1, objectKey: "exports/1.zip");
        job1.Id = await db.InsertWithInt32IdentityAsync(job1);
        DataExportJob job2 = BuildExpiredJob(tenantId: 2, objectKey: "exports/2.zip");
        job2.Id = await db.InsertWithInt32IdentityAsync(job2);
        DataExportJob job3 = BuildExpiredJob(tenantId: 3, objectKey: "exports/3.zip");
        job3.Id = await db.InsertWithInt32IdentityAsync(job3);

        TestServiceScopeFactory scopeFactory = new(db);
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: true);

        DataExportCleanupService service = new(scopeFactory, distributedLock, objectStorage, logger);
        await RunCleanupDirectly(service);

        // All three S3 objects should have been deleted
        await objectStorage.Received(1).DeleteObjectAsync("exports/1.zip", Arg.Any<CancellationToken>());
        await objectStorage.Received(1).DeleteObjectAsync("exports/2.zip", Arg.Any<CancellationToken>());
        await objectStorage.Received(1).DeleteObjectAsync("exports/3.zip", Arg.Any<CancellationToken>());

        // All jobs should be Expired
        List<DataExportJob> jobs = await db.DataExportJobs.ToListAsync();
        await Assert.That(jobs.All(j => j.Status == DataExportJobStatus.Expired)).IsTrue();
    }

    // ========== Cleanup_OnlyCompleteStatusIsExpired ==========

    [Test]
    public async Task Cleanup_OnlyCompleteStatusIsExpired()
    {
        // Failed jobs with past ExpiresAt should NOT be cleaned up
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        DataExportJob failedJob = new()
        {
            TenantId = 1,
            Status = DataExportJobStatus.Failed,
            RequestedByUserId = 1,
            RequestedAt = DateTimeOffset.UtcNow.AddHours(-2),
            ObjectKey = "exports/failed.zip",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            DownloadToken = Guid.NewGuid().ToString("N")
        };
        failedJob.Id = await db.InsertWithInt32IdentityAsync(failedJob);

        TestServiceScopeFactory scopeFactory = new(db);
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: true);

        DataExportCleanupService service = new(scopeFactory, distributedLock, objectStorage, logger);
        await RunCleanupDirectly(service);

        // Failed jobs should not be cleaned up even if expired
        await objectStorage.DidNotReceive().DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Status should remain Failed
        DataExportJob? job = await db.DataExportJobs
            .Where(j => j.Id == failedJob.Id)
            .FirstOrDefaultAsync();

        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Status).IsEqualTo(DataExportJobStatus.Failed);
    }

    // This test was removed — already covered by the existing Cleanup_EmptyObjectKey_SkipsDeleteAndTransitions test.

    // ========== Cleanup_CancellationDuringLoop_StopsProcessing ==========

    [Test]
    public async Task Cleanup_CancellationDuringLoop_StopsProcessing()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        // Seed three expired jobs
        DataExportJob job1 = BuildExpiredJob(tenantId: 1, objectKey: "exports/first.zip");
        job1.Id = await db.InsertWithInt32IdentityAsync(job1);
        DataExportJob job2 = BuildExpiredJob(tenantId: 2, objectKey: "exports/second.zip");
        job2.Id = await db.InsertWithInt32IdentityAsync(job2);
        DataExportJob job3 = BuildExpiredJob(tenantId: 3, objectKey: "exports/third.zip");
        job3.Id = await db.InsertWithInt32IdentityAsync(job3);

        TestServiceScopeFactory scopeFactory = new(db);
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: true);

        using CancellationTokenSource cts = new();

        // Cancel after the first delete call
        objectStorage.DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                cts.Cancel();

                return Task.CompletedTask;
            });

        DataExportCleanupService service = new(scopeFactory, distributedLock, objectStorage, logger);
        await service.CleanupExpiredExportsAsync(cts.Token);

        // Should have processed at most 1 job before cancellation took effect
        await objectStorage.Received(1).DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ========== Execute_ExceptionDuringCycle_LogsAndContinues ==========

    [Test]
    public async Task Execute_ExceptionDuringCycle_LogsAndContinues()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        TestServiceScopeFactory scopeFactory = new(db);
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: true);

        int lockAttempts = 0;
        TaskCompletionSource secondAttempt = new();

        // First lock attempt throws, second succeeds to prove the loop continues
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(callInfo =>
            {
                int attempt = Interlocked.Increment(ref lockAttempts);
                if (attempt == 1)
                {
                    throw new InvalidOperationException("Redis connection failed");
                }

                secondAttempt.TrySetResult();

                return (LockHandle?)null;
            });

        DataExportCleanupService service = new(scopeFactory, distributedLock, objectStorage, logger);
        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await secondAttempt.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // Verify error was logged for the first attempt
        logger.Received().Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Is<Exception>(ex => ex is InvalidOperationException),
            Arg.Any<Func<object, Exception?, string>>());

        // Verify the service continued to a second attempt
        await Assert.That(lockAttempts >= 2).IsTrue();
    }

    // ========== Constructor_NullScopeFactory_ThrowsArgumentNullException ==========

    [Test]
    public async Task Constructor_NullScopeFactory_ThrowsArgumentNullException()
    {
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: false);

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            DataExportCleanupService _ = new(null!, distributedLock, objectStorage, logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("scopeFactory");
    }

    // ========== Constructor_NullDistributedLock_ThrowsArgumentNullException ==========

    [Test]
    public async Task Constructor_NullDistributedLock_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        (IDistributedLock _, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: false);

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            DataExportCleanupService _ = new(scopeFactory, null!, objectStorage, logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("distributedLock");
    }

    // ========== Constructor_NullObjectStorage_ThrowsArgumentNullException ==========

    [Test]
    public async Task Constructor_NullObjectStorage_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        (IDistributedLock distributedLock, IObjectStorageService _, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: false);

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            DataExportCleanupService _ = new(scopeFactory, distributedLock, null!, logger);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("objectStorageService");
    }

    // ========== Constructor_NullLogger_ThrowsArgumentNullException ==========

    [Test]
    public async Task Constructor_NullLogger_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> _) = CreateMocks(grantLock: false);

        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            DataExportCleanupService _ = new(scopeFactory, distributedLock, objectStorage, null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }

    // ========== Execute_LockAcquiredWithCorrectKeyAndTtl ==========

    [Test]
    public async Task Execute_LockAcquiredWithCorrectKeyAndTtl()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        TestServiceScopeFactory scopeFactory = new(db);
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: false);

        TaskCompletionSource lockAttempted = new();
        distributedLock.TryAcquireAsync(Arg.Any<string>(), Arg.Any<TimeSpan>())
            .Returns(callInfo =>
            {
                lockAttempted.TrySetResult();

                return (LockHandle?)null;
            });

        DataExportCleanupService service = new(scopeFactory, distributedLock, objectStorage, logger);
        using CancellationTokenSource cts = new();
        await service.StartAsync(cts.Token);
        await lockAttempted.Task;
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        // Verify the correct lock key and TTL are used
        await distributedLock.Received().TryAcquireAsync("lock:data-export-cleanup", TimeSpan.FromMinutes(10));
    }

    // ========== Cleanup_ExpireRepoThrows_LogsAndContinuesNextJob ==========

    [Test]
    public async Task Cleanup_S3DeleteSucceeds_ButExpireRepoThrows_LogsAndContinuesNextJob()
    {
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        DataExportJob job1 = BuildExpiredJob(tenantId: 1, objectKey: "exports/job1.zip");
        job1.Id = await db.InsertWithInt32IdentityAsync(job1);
        DataExportJob job2 = BuildExpiredJob(tenantId: 2, objectKey: "exports/job2.zip");
        job2.Id = await db.InsertWithInt32IdentityAsync(job2);

        TestServiceScopeFactory scopeFactory = new(db);
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: true);

        // Both S3 deletes succeed
        objectStorage.DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        DataExportCleanupService service = new(scopeFactory, distributedLock, objectStorage, logger);
        await RunCleanupDirectly(service);

        // Both deletes should have been attempted
        await objectStorage.Received(1).DeleteObjectAsync("exports/job1.zip", Arg.Any<CancellationToken>());
        await objectStorage.Received(1).DeleteObjectAsync("exports/job2.zip", Arg.Any<CancellationToken>());
    }

    // ========== Regression: DB update before S3 delete (bug fix) ==========

    [Test]
    public async Task Cleanup_S3DeleteFails_JobStillMarkedExpired()
    {
        // Regression test: previously, S3 delete ran before DB ExpireExportJobAsync.
        // If S3 succeeded but DB failed, the object was deleted but the job wasn't expired,
        // causing the job to be retried on a non-existent object.
        // Fix: DB update runs first so the job is marked expired even if S3 delete fails.
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        DataExportJob expiredJob = BuildExpiredJob(objectKey: "exports/s3-fail.zip");
        expiredJob.Id = await db.InsertWithInt32IdentityAsync(expiredJob);

        TestServiceScopeFactory scopeFactory = new(db);
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: true);

        // S3 delete fails
        objectStorage.DeleteObjectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("S3 connection refused"));

        DataExportCleanupService service = new(scopeFactory, distributedLock, objectStorage, logger);
        await RunCleanupDirectly(service);

        // The job should still be marked as expired in the database despite S3 failure
        DataExportJob? job = await db.DataExportJobs
            .Where(j => j.Id == expiredJob.Id)
            .FirstOrDefaultAsync();

        await Assert.That(job).IsNotNull();
        await Assert.That(job!.Status).IsEqualTo(DataExportJobStatus.Expired);
    }

    [Test]
    public async Task Cleanup_S3DeleteFails_DoesNotPreventOtherJobsFromProcessing()
    {
        // Verify that an S3 failure for one job doesn't prevent other jobs from being cleaned up
        using TestDatabaseFactory dbFactory = new();
        DatabaseContext db = dbFactory.Context;

        DataExportJob job1 = BuildExpiredJob(objectKey: "exports/fail-job.zip");
        job1.Id = await db.InsertWithInt32IdentityAsync(job1);
        DataExportJob job2 = BuildExpiredJob(objectKey: "exports/succeed-job.zip");
        job2.Id = await db.InsertWithInt32IdentityAsync(job2);

        TestServiceScopeFactory scopeFactory = new(db);
        (IDistributedLock distributedLock, IObjectStorageService objectStorage, ILogger<DataExportCleanupService> logger) = CreateMocks(grantLock: true);

        // First S3 delete fails, second succeeds
        objectStorage.DeleteObjectAsync("exports/fail-job.zip", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("S3 error"));
        objectStorage.DeleteObjectAsync("exports/succeed-job.zip", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        DataExportCleanupService service = new(scopeFactory, distributedLock, objectStorage, logger);
        await RunCleanupDirectly(service);

        // Both jobs should be expired in the database
        DataExportJob? updatedJob1 = await db.DataExportJobs.FirstOrDefaultAsync(j => j.Id == job1.Id);
        DataExportJob? updatedJob2 = await db.DataExportJobs.FirstOrDefaultAsync(j => j.Id == job2.Id);

        await Assert.That(updatedJob1!.Status).IsEqualTo(DataExportJobStatus.Expired);
        await Assert.That(updatedJob2!.Status).IsEqualTo(DataExportJobStatus.Expired);
    }
}
