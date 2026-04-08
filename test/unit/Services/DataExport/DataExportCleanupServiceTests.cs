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
}
