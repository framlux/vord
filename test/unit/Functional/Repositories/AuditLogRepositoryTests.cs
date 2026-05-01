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
/// Functional tests for audit-log-related methods on <see cref="Database.Repositories.DatabaseRepository"/>.
/// </summary>
public class AuditLogRepositoryTests
{
    /// <summary>
    /// Creates a user and tenant in the database, returning their IDs.
    /// Audit log tests often require these prerequisite records.
    /// </summary>
    private static async Task<(int userId, int tenantId)> SeedUserAndTenantAsync(TestDatabaseFactory dbFactory)
    {
        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        return (userId, tenantId);
    }

    /// <summary>
    /// Builds an <see cref="AuditLogEntry"/> with sensible defaults for testing.
    /// </summary>
    private static AuditLogEntry BuildAuditLogEntry(
        int? tenantId = null,
        int? userId = null,
        AuditAction action = AuditAction.UserLogin,
        AuditResourceType resourceType = AuditResourceType.User,
        string? resourceId = null,
        DateTimeOffset? timestamp = null)
    {
        return new AuditLogEntry
        {
            TenantId = tenantId,
            UserId = userId,
            MachineId = null,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId ?? "test-resource-1",
            Details = null,
            IpAddress = "127.0.0.1",
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
        };
    }

    // ========== InsertAuditLogAsync tests ==========

    [Test]
    public async Task InsertAuditLogAsync_ValidEntry_InsertsSuccessfully()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AuditLogEntry entry = BuildAuditLogEntry(
            tenantId: tenantId,
            userId: userId,
            action: AuditAction.UserLogin);

        await repo.InsertAuditLogAsync(entry);

        // Verify by querying back
        List<AuditLogEntry> entries = await repo.GetAuditLogEntriesForTenantAsync(
            tenantId, skip: 0, take: 10, actionFilter: null, fromDate: null, toDate: null);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Action).IsEqualTo(AuditAction.UserLogin);
        await Assert.That(entries[0].TenantId).IsEqualTo(tenantId);
        await Assert.That(entries[0].UserId).IsEqualTo(userId);
    }

    [Test]
    public async Task InsertAuditLogAsync_NullEntry_ThrowsArgumentNullException()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await Assert.That(async () => await repo.InsertAuditLogAsync(null!)).Throws<ArgumentNullException>();
    }

    // ========== GetAuditLogEntriesForTenantAsync tests ==========

    [Test]
    public async Task GetAuditLogEntriesForTenantAsync_EntriesExist_ReturnsEntriesOrderedByTimestampDesc()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AuditLogEntry entry1 = BuildAuditLogEntry(tenantId: tenantId, userId: userId,
            action: AuditAction.UserLogin, timestamp: DateTimeOffset.UtcNow.AddHours(-2));
        await repo.InsertAuditLogAsync(entry1);

        AuditLogEntry entry2 = BuildAuditLogEntry(tenantId: tenantId, userId: userId,
            action: AuditAction.MachineRegistered, timestamp: DateTimeOffset.UtcNow.AddHours(-1));
        await repo.InsertAuditLogAsync(entry2);

        AuditLogEntry entry3 = BuildAuditLogEntry(tenantId: tenantId, userId: userId,
            action: AuditAction.TenantCreated, timestamp: DateTimeOffset.UtcNow);
        await repo.InsertAuditLogAsync(entry3);

        List<AuditLogEntry> result = await repo.GetAuditLogEntriesForTenantAsync(
            tenantId, skip: 0, take: 10, actionFilter: null, fromDate: null, toDate: null);

        await Assert.That(result.Count).IsEqualTo(3);
        // Verify descending order by timestamp
        await Assert.That(result[0].Action).IsEqualTo(AuditAction.TenantCreated);
        await Assert.That(result[2].Action).IsEqualTo(AuditAction.UserLogin);
    }

    [Test]
    public async Task GetAuditLogEntriesForTenantAsync_Pagination_ReturnsCorrectPage()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        for (int i = 0; i < 5; i++)
        {
            AuditLogEntry entry = BuildAuditLogEntry(tenantId: tenantId, userId: userId,
                timestamp: DateTimeOffset.UtcNow.AddMinutes(-10 + i));
            await repo.InsertAuditLogAsync(entry);
        }

        List<AuditLogEntry> page1 = await repo.GetAuditLogEntriesForTenantAsync(
            tenantId, skip: 0, take: 2, actionFilter: null, fromDate: null, toDate: null);
        List<AuditLogEntry> page2 = await repo.GetAuditLogEntriesForTenantAsync(
            tenantId, skip: 2, take: 2, actionFilter: null, fromDate: null, toDate: null);
        List<AuditLogEntry> page3 = await repo.GetAuditLogEntriesForTenantAsync(
            tenantId, skip: 4, take: 2, actionFilter: null, fromDate: null, toDate: null);

        await Assert.That(page1.Count).IsEqualTo(2);
        await Assert.That(page2.Count).IsEqualTo(2);
        await Assert.That(page3.Count).IsEqualTo(1);
    }

    [Test]
    public async Task GetAuditLogEntriesForTenantAsync_ActionFilter_ReturnsOnlyMatchingAction()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId,
            action: AuditAction.UserLogin));
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId,
            action: AuditAction.MachineRegistered));
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId,
            action: AuditAction.UserLogin));

        List<AuditLogEntry> result = await repo.GetAuditLogEntriesForTenantAsync(
            tenantId, skip: 0, take: 10, actionFilter: AuditAction.UserLogin, fromDate: null, toDate: null);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].Action).IsEqualTo(AuditAction.UserLogin);
        await Assert.That(result[1].Action).IsEqualTo(AuditAction.UserLogin);
    }

    [Test]
    public async Task GetAuditLogEntriesForTenantAsync_DateRangeFilter_ReturnsOnlyInRange()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId,
            action: AuditAction.UserLogin, timestamp: now.AddDays(-3)));
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId,
            action: AuditAction.MachineRegistered, timestamp: now.AddDays(-1)));
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId,
            action: AuditAction.TenantCreated, timestamp: now));

        List<AuditLogEntry> result = await repo.GetAuditLogEntriesForTenantAsync(
            tenantId, skip: 0, take: 10, actionFilter: null,
            fromDate: now.AddDays(-2), toDate: now.AddHours(-1));

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Action).IsEqualTo(AuditAction.MachineRegistered);
    }

    [Test]
    public async Task GetAuditLogEntriesForTenantAsync_CombinedFilters_ReturnsCorrectSubset()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Login 3 days ago - outside date range
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId,
            action: AuditAction.UserLogin, timestamp: now.AddDays(-3)));
        // Login 1 day ago - matches both filters
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId,
            action: AuditAction.UserLogin, timestamp: now.AddDays(-1)));
        // Machine registered 1 day ago - matches date but not action
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId,
            action: AuditAction.MachineRegistered, timestamp: now.AddDays(-1)));

        List<AuditLogEntry> result = await repo.GetAuditLogEntriesForTenantAsync(
            tenantId, skip: 0, take: 10, actionFilter: AuditAction.UserLogin,
            fromDate: now.AddDays(-2), toDate: now);

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Action).IsEqualTo(AuditAction.UserLogin);
    }

    [Test]
    public async Task GetAuditLogEntriesForTenantAsync_DifferentTenant_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId,
            action: AuditAction.UserLogin));

        int otherTenantId = tenantId + 1000;
        List<AuditLogEntry> result = await repo.GetAuditLogEntriesForTenantAsync(
            otherTenantId, skip: 0, take: 10, actionFilter: null, fromDate: null, toDate: null);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== CountAuditLogEntriesForTenantAsync tests ==========

    [Test]
    public async Task CountAuditLogEntriesForTenantAsync_EntriesExist_ReturnsCorrectCount()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId));
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId));
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId));

        int count = await repo.CountAuditLogEntriesForTenantAsync(
            tenantId, actionFilter: null, fromDate: null, toDate: null);

        await Assert.That(count).IsEqualTo(3);
    }

    [Test]
    public async Task CountAuditLogEntriesForTenantAsync_WithActionFilter_CountsOnlyMatching()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId,
            action: AuditAction.UserLogin));
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId,
            action: AuditAction.MachineRegistered));
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId,
            action: AuditAction.UserLogin));

        int count = await repo.CountAuditLogEntriesForTenantAsync(
            tenantId, actionFilter: AuditAction.UserLogin, fromDate: null, toDate: null);

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task CountAuditLogEntriesForTenantAsync_NoEntries_ReturnsZero()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        int count = await repo.CountAuditLogEntriesForTenantAsync(
            99999, actionFilter: null, fromDate: null, toDate: null);

        await Assert.That(count).IsEqualTo(0);
    }

    // ========== QueryAuditLogEntriesAsync tests ==========

    [Test]
    public async Task QueryAuditLogEntriesAsync_WithTenantFilter_ReturnsOnlyTenantEntries()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId));
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId));

        // Insert an entry with a different tenant ID
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId + 1000, userId: userId));

        (List<AuditLogEntry> entries, int totalCount) = await repo.QueryAuditLogEntriesAsync(
            tenantId: tenantId, skip: 0, take: 10);

        await Assert.That(entries.Count).IsEqualTo(2);
        await Assert.That(totalCount).IsEqualTo(2);
    }

    [Test]
    public async Task QueryAuditLogEntriesAsync_WithoutTenantFilter_ReturnsAllEntries()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId));
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId + 1000, userId: userId));
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: null, userId: userId));

        (List<AuditLogEntry> entries, int totalCount) = await repo.QueryAuditLogEntriesAsync(
            tenantId: null, skip: 0, take: 10);

        await Assert.That(entries.Count).IsEqualTo(3);
        await Assert.That(totalCount).IsEqualTo(3);
    }

    [Test]
    public async Task QueryAuditLogEntriesAsync_Pagination_ReturnsCorrectPageAndTotalCount()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        for (int i = 0; i < 5; i++)
        {
            await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId,
                timestamp: DateTimeOffset.UtcNow.AddMinutes(-10 + i)));
        }

        (List<AuditLogEntry> entries, int totalCount) = await repo.QueryAuditLogEntriesAsync(
            tenantId: tenantId, skip: 0, take: 2);

        await Assert.That(entries.Count).IsEqualTo(2);
        await Assert.That(totalCount).IsEqualTo(5);
    }

    // ========== GetAuditLogBatchAsync tests ==========

    [Test]
    public async Task GetAuditLogBatchAsync_EntriesExist_ReturnsOrderedByIdAscending()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId));
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId));
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId));

        List<AuditLogEntry> batch = await repo.GetAuditLogBatchAsync(tenantId, afterId: 0, batchSize: 10);

        await Assert.That(batch.Count).IsEqualTo(3);
        // Verify ascending order by Id
        await Assert.That(batch[0].Id < batch[1].Id).IsTrue();
        await Assert.That(batch[1].Id < batch[2].Id).IsTrue();
    }

    [Test]
    public async Task GetAuditLogBatchAsync_CursorBasedPaging_ReturnsEntriesAfterCursor()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId));
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId));
        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId));

        // Get first batch to establish a cursor
        List<AuditLogEntry> firstBatch = await repo.GetAuditLogBatchAsync(tenantId, afterId: 0, batchSize: 2);

        await Assert.That(firstBatch.Count).IsEqualTo(2);

        long cursor = firstBatch[^1].Id;
        List<AuditLogEntry> secondBatch = await repo.GetAuditLogBatchAsync(tenantId, afterId: cursor, batchSize: 10);

        await Assert.That(secondBatch.Count).IsEqualTo(1);
        await Assert.That(secondBatch[0].Id > cursor).IsTrue();
    }

    [Test]
    public async Task GetAuditLogBatchAsync_AfterLastEntry_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId));

        // Get the one entry to find the max ID
        List<AuditLogEntry> allEntries = await repo.GetAuditLogBatchAsync(tenantId, afterId: 0, batchSize: 10);
        long maxId = allEntries[^1].Id;

        List<AuditLogEntry> result = await repo.GetAuditLogBatchAsync(tenantId, afterId: maxId, batchSize: 10);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetAuditLogBatchAsync_DifferentTenant_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        await repo.InsertAuditLogAsync(BuildAuditLogEntry(tenantId: tenantId, userId: userId));

        int otherTenantId = tenantId + 1000;
        List<AuditLogEntry> result = await repo.GetAuditLogBatchAsync(otherTenantId, afterId: 0, batchSize: 10);

        await Assert.That(result.Count).IsEqualTo(0);
    }
}
