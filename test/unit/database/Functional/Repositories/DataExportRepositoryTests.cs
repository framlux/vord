// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for data export job methods on <see cref="Database.Repositories.DatabaseRepository"/>.
/// </summary>
public class DataExportRepositoryTests
{
    /// <summary>
    /// Creates a user and tenant in the database, returning their IDs.
    /// Many export job tests require these prerequisite records.
    /// </summary>
    private static async Task<(int userId, int tenantId)> SeedUserAndTenantAsync(TestDatabaseFactory dbFactory)
    {
        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        return (userId, tenantId);
    }

    [Test]
    public async Task CreateExportJobAsync_ValidJob_ReturnsJobWithGeneratedId()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        DataExportJob job = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId);

        DataExportJob result = await repo.CreateExportJobAsync(job);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsNotEqualTo(0);
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
        await Assert.That(result.RequestedByUserId).IsEqualTo(userId);
        await Assert.That(result.Status).IsEqualTo(DataExportJobStatus.Pending);
    }

    [Test]
    public async Task GetExportJobByIdAsync_ExistingJob_ReturnsJob()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        DataExportJob job = TestDataBuilder.BuildDataExportJob(tenantId: tenantId, requestedByUserId: userId);
        int jobId = await dbFactory.Context.InsertWithInt32IdentityAsync(job);

        DataExportJob? result = await repo.GetExportJobByIdAsync(jobId);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(jobId);
        await Assert.That(result.TenantId).IsEqualTo(tenantId);
    }

    [Test]
    public async Task GetExportJobByIdAsync_NonExistentId_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DataExportJob? result = await repo.GetExportJobByIdAsync(99999);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetExportJobByTokenAsync_ExistingToken_ReturnsJob()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        string downloadToken = "unique-download-token-abc";
        DataExportJob job = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            downloadToken: downloadToken);
        int jobId = await dbFactory.Context.InsertWithInt32IdentityAsync(job);

        DataExportJob? result = await repo.GetExportJobByTokenAsync(downloadToken);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(jobId);
        await Assert.That(result.DownloadToken).IsEqualTo(downloadToken);
    }

    [Test]
    public async Task GetExportJobByTokenAsync_NonExistentToken_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        DataExportJob? result = await repo.GetExportJobByTokenAsync("nonexistent-token-xyz");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task HasActiveExportJobAsync_PendingJob_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        DataExportJob job = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Pending);
        await dbFactory.Context.InsertWithInt32IdentityAsync(job);

        bool hasActive = await repo.HasActiveExportJobAsync(tenantId);

        await Assert.That(hasActive).IsTrue();
    }

    [Test]
    public async Task HasActiveExportJobAsync_ProcessingJob_ReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        DataExportJob job = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Processing);
        await dbFactory.Context.InsertWithInt32IdentityAsync(job);

        bool hasActive = await repo.HasActiveExportJobAsync(tenantId);

        await Assert.That(hasActive).IsTrue();
    }

    [Test]
    public async Task HasActiveExportJobAsync_NoJobs_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        bool hasActive = await repo.HasActiveExportJobAsync(99999);

        await Assert.That(hasActive).IsFalse();
    }

    [Test]
    public async Task HasActiveExportJobAsync_OnlyCompletedJobs_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        DataExportJob completedJob = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Complete);
        await dbFactory.Context.InsertWithInt32IdentityAsync(completedJob);

        DataExportJob failedJob = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Failed);
        await dbFactory.Context.InsertWithInt32IdentityAsync(failedJob);

        bool hasActive = await repo.HasActiveExportJobAsync(tenantId);

        await Assert.That(hasActive).IsFalse();
    }

    [Test]
    public async Task UpdateExportJobStatusAsync_TransitionsStatus()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        DataExportJob job = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Pending);
        int jobId = await dbFactory.Context.InsertWithInt32IdentityAsync(job);

        await repo.UpdateExportJobStatusAsync(jobId, DataExportJobStatus.Processing);

        DataExportJob? updated = await repo.GetExportJobByIdAsync(jobId);

        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Status).IsEqualTo(DataExportJobStatus.Processing);
    }

    [Test]
    public async Task CompleteExportJobAsync_SetsObjectKeyFileSizeAndStatus()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        DataExportJob job = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Processing);
        int jobId = await dbFactory.Context.InsertWithInt32IdentityAsync(job);

        await repo.CompleteExportJobAsync(jobId, "exports/tenant-1/data.zip", 1048576L);

        DataExportJob? completed = await repo.GetExportJobByIdAsync(jobId);

        await Assert.That(completed).IsNotNull();
        await Assert.That(completed!.Status).IsEqualTo(DataExportJobStatus.Complete);
        await Assert.That(completed.ObjectKey).IsEqualTo("exports/tenant-1/data.zip");
        await Assert.That(completed.FileSizeBytes).IsEqualTo(1048576L);
        await Assert.That(completed.CompletedAt).IsNotNull();
    }

    [Test]
    public async Task FailExportJobAsync_SetsErrorMessageAndStatus()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        DataExportJob job = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Processing);
        int jobId = await dbFactory.Context.InsertWithInt32IdentityAsync(job);

        await repo.FailExportJobAsync(jobId, "Disk space exhausted during export");

        DataExportJob? failed = await repo.GetExportJobByIdAsync(jobId);

        await Assert.That(failed).IsNotNull();
        await Assert.That(failed!.Status).IsEqualTo(DataExportJobStatus.Failed);
        await Assert.That(failed.ErrorMessage).IsEqualTo("Disk space exhausted during export");
    }

    [Test]
    public async Task GetPendingExportJobsAsync_ReturnsOnlyPendingOrderedByRequestedAt()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        DataExportJob pendingOlder = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Pending,
            requestedAt: DateTimeOffset.UtcNow.AddHours(-2));
        await dbFactory.Context.InsertWithInt32IdentityAsync(pendingOlder);

        DataExportJob pendingNewer = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Pending,
            requestedAt: DateTimeOffset.UtcNow.AddHours(-1));
        await dbFactory.Context.InsertWithInt32IdentityAsync(pendingNewer);

        DataExportJob processingJob = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Processing);
        await dbFactory.Context.InsertWithInt32IdentityAsync(processingJob);

        DataExportJob completedJob = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Complete);
        await dbFactory.Context.InsertWithInt32IdentityAsync(completedJob);

        List<DataExportJob> result = await repo.GetPendingExportJobsAsync();

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].RequestedAt <= result[1].RequestedAt).IsTrue();
    }

    [Test]
    public async Task GetPendingExportJobsAsync_NoPendingJobs_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        DataExportJob completedJob = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Complete);
        await dbFactory.Context.InsertWithInt32IdentityAsync(completedJob);

        List<DataExportJob> result = await repo.GetPendingExportJobsAsync();

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetExpiredExportJobsAsync_ReturnsExpiredCompletedJobs()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        // Expired completed job (expires in the past)
        DataExportJob expiredJob = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Complete,
            expiresAt: DateTimeOffset.UtcNow.AddHours(-1));
        await dbFactory.Context.InsertWithInt32IdentityAsync(expiredJob);

        // Non-expired completed job (expires in the future)
        DataExportJob activeJob = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Complete,
            expiresAt: DateTimeOffset.UtcNow.AddHours(24));
        await dbFactory.Context.InsertWithInt32IdentityAsync(activeJob);

        // Pending job that is past its expiry (should not be returned since it is not Complete)
        DataExportJob pendingExpired = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Pending,
            expiresAt: DateTimeOffset.UtcNow.AddHours(-1));
        await dbFactory.Context.InsertWithInt32IdentityAsync(pendingExpired);

        List<DataExportJob> result = await repo.GetExpiredExportJobsAsync();

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Status).IsEqualTo(DataExportJobStatus.Complete);
    }

    [Test]
    public async Task GetExpiredExportJobsAsync_NoExpiredJobs_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        // Non-expired completed job
        DataExportJob activeJob = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Complete,
            expiresAt: DateTimeOffset.UtcNow.AddHours(24));
        await dbFactory.Context.InsertWithInt32IdentityAsync(activeJob);

        List<DataExportJob> result = await repo.GetExpiredExportJobsAsync();

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExpireExportJobAsync_TransitionsToExpiredStatus()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        DataExportJob job = TestDataBuilder.BuildDataExportJob(
            tenantId: tenantId,
            requestedByUserId: userId,
            status: DataExportJobStatus.Complete);
        int jobId = await dbFactory.Context.InsertWithInt32IdentityAsync(job);

        await repo.ExpireExportJobAsync(jobId);

        DataExportJob? expired = await repo.GetExportJobByIdAsync(jobId);

        await Assert.That(expired).IsNotNull();
        await Assert.That(expired!.Status).IsEqualTo(DataExportJobStatus.Expired);
    }

    [Test]
    public async Task TryClaimPendingJobAsync_PendingJob_TransitionsToProcessingAndReturnsTrue()
    {
        // Intent: the claim is the atomic transition Pending → Processing. The caller that wins
        // the claim sees true and proceeds; concurrent callers see false and exit cleanly.
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);
        DataExportJob job = TestDataBuilder.BuildDataExportJob(tenantId: tenantId, requestedByUserId: userId);
        int jobId = await dbFactory.Context.InsertWithInt32IdentityAsync(job);

        DateTimeOffset claimAt = new(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);
        bool claimed = await repo.TryClaimPendingJobAsync(jobId, claimAt, CancellationToken.None);

        await Assert.That(claimed).IsTrue();
        DataExportJob? after = await repo.GetExportJobByIdAsync(jobId);
        await Assert.That(after!.Status).IsEqualTo(DataExportJobStatus.Processing);
        await Assert.That(after.StartedAt).IsEqualTo(claimAt);
    }

    [Test]
    public async Task TryClaimPendingJobAsync_AlreadyProcessing_ReturnsFalseAndDoesNotChangeRow()
    {
        // Intent: the second worker to attempt claim must see false (so it doesn't double-process)
        // AND must not clobber the StartedAt of the first claimant.
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);
        DataExportJob job = TestDataBuilder.BuildDataExportJob(tenantId: tenantId, requestedByUserId: userId);
        int jobId = await dbFactory.Context.InsertWithInt32IdentityAsync(job);

        DateTimeOffset first = new(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);
        await repo.TryClaimPendingJobAsync(jobId, first, CancellationToken.None);

        DateTimeOffset second = new(2026, 5, 18, 12, 30, 0, TimeSpan.Zero);
        bool secondClaimed = await repo.TryClaimPendingJobAsync(jobId, second, CancellationToken.None);

        await Assert.That(secondClaimed).IsFalse();
        DataExportJob? after = await repo.GetExportJobByIdAsync(jobId);
        await Assert.That(after!.StartedAt).IsEqualTo(first);
    }

    [Test]
    public async Task TryClaimPendingJobAsync_NonExistentId_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        bool claimed = await repo.TryClaimPendingJobAsync(jobId: 99999, DateTimeOffset.UtcNow, CancellationToken.None);

        await Assert.That(claimed).IsFalse();
    }

    [Test]
    public async Task GetStuckProcessingJobsAsync_ReturnsOnlyOldProcessingRows()
    {
        // Intent: only Processing rows with StartedAt older than the threshold are returned.
        // Recently-started Processing rows and Pending rows must NOT come back.
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);
        DateTimeOffset now = new(2026, 5, 18, 13, 0, 0, TimeSpan.Zero);

        DataExportJob stuck = TestDataBuilder.BuildDataExportJob(tenantId: tenantId, requestedByUserId: userId, status: DataExportJobStatus.Processing);
        stuck.StartedAt = now.AddHours(-2);
        int stuckId = await dbFactory.Context.InsertWithInt32IdentityAsync(stuck);

        DataExportJob fresh = TestDataBuilder.BuildDataExportJob(tenantId: tenantId, requestedByUserId: userId, status: DataExportJobStatus.Processing);
        fresh.StartedAt = now.AddMinutes(-5);
        await dbFactory.Context.InsertWithInt32IdentityAsync(fresh);

        DataExportJob pending = TestDataBuilder.BuildDataExportJob(tenantId: tenantId, requestedByUserId: userId);
        await dbFactory.Context.InsertWithInt32IdentityAsync(pending);

        DateTimeOffset cutoff = now.AddHours(-1);
        List<DataExportJob> stuckJobs = await repo.GetStuckProcessingJobsAsync(cutoff, CancellationToken.None);

        await Assert.That(stuckJobs.Count).IsEqualTo(1);
        await Assert.That(stuckJobs[0].Id).IsEqualTo(stuckId);
    }

    [Test]
    public async Task ResetOrphanedJobToPendingAsync_ProcessingRow_TransitionsBackToPending()
    {
        // Intent: an orphaned Processing row (worker crashed) must be returnable to Pending so
        // the next tick picks it up. The StartedAt is cleared as part of the reset.
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        DataExportJob stuck = TestDataBuilder.BuildDataExportJob(tenantId: tenantId, requestedByUserId: userId, status: DataExportJobStatus.Processing);
        stuck.StartedAt = DateTimeOffset.UtcNow.AddHours(-3);
        int stuckId = await dbFactory.Context.InsertWithInt32IdentityAsync(stuck);

        await repo.ResetOrphanedJobToPendingAsync(stuckId, CancellationToken.None);

        DataExportJob? after = await repo.GetExportJobByIdAsync(stuckId);
        await Assert.That(after!.Status).IsEqualTo(DataExportJobStatus.Pending);
        await Assert.That(after.StartedAt).IsNull();
    }

    [Test]
    public async Task ResetOrphanedJobToPendingAsync_CompletedRow_IsNoOpAndDoesNotClobber()
    {
        // Intent: a row that completed concurrently must not be moved back to Pending — that
        // would cause the work to re-run. The reset is conditional on Status=Processing.
        using TestDatabaseFactory dbFactory = new();
        IDataExportRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        DataExportJob done = TestDataBuilder.BuildDataExportJob(tenantId: tenantId, requestedByUserId: userId, status: DataExportJobStatus.Complete);
        int doneId = await dbFactory.Context.InsertWithInt32IdentityAsync(done);

        await repo.ResetOrphanedJobToPendingAsync(doneId, CancellationToken.None);

        DataExportJob? after = await repo.GetExportJobByIdAsync(doneId);
        await Assert.That(after!.Status).IsEqualTo(DataExportJobStatus.Complete);
    }
}
