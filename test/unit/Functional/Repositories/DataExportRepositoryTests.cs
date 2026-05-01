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
}
