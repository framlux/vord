// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using DbRepo = Framlux.FleetManagement.Database.Repositories.DatabaseRepository;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Tests for the <see cref="IAuditContextAccessor"/> integration inside
/// <see cref="Framlux.FleetManagement.Database.Repositories.DatabaseRepository"/>.
/// Verifies that <see cref="IAuditLogRepository.InsertAuditLogAsync"/> populates
/// <see cref="AuditLogEntry.IpAddress"/> from the accessor when the entry does not
/// supply an explicit IP, and that an existing non-null IP is never overwritten.
/// </summary>
public class AuditContextAccessorTests
{
    private static async Task<(int userId, int tenantId)> SeedUserAndTenantAsync(TestDatabaseFactory dbFactory)
    {
        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        return (userId, tenantId);
    }

    // ========== NullAuditContextAccessor — default behaviour ==========

    [Test]
    public async Task InsertAuditLogAsync_NullAuditContextAccessor_IpAddressRemainsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IAuditLogRepository repo = new DbRepo(
            dbFactory.Context,
            new NullLogger<DbRepo>(),
            new NullAuditContextAccessor());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AuditLogEntry entry = new()
        {
            TenantId = tenantId,
            UserId = userId,
            MachineId = null,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            ResourceId = "user-1",
            Details = null,
            IpAddress = null,
            Timestamp = DateTimeOffset.UtcNow,
        };

        await repo.InsertAuditLogAsync(entry);

        List<AuditLogEntry> entries = await repo.GetAuditLogEntriesForTenantAsync(
            tenantId, skip: 0, take: 10, actionFilter: null, fromDate: null, toDate: null);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].IpAddress).IsNull();
    }

    // ========== Accessor supplies IP — null entry IP is populated ==========

    [Test]
    public async Task InsertAuditLogAsync_NullEntryIp_PopulatesIpFromAccessor()
    {
        using TestDatabaseFactory dbFactory = new();

        IAuditContextAccessor stubAccessor = Substitute.For<IAuditContextAccessor>();
        stubAccessor.GetClientIp().Returns("10.0.0.1");

        IAuditLogRepository repo = new DbRepo(
            dbFactory.Context,
            new NullLogger<DbRepo>(),
            stubAccessor);

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AuditLogEntry entry = new()
        {
            TenantId = tenantId,
            UserId = userId,
            MachineId = null,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            ResourceId = "user-1",
            Details = null,
            IpAddress = null,
            Timestamp = DateTimeOffset.UtcNow,
        };

        await repo.InsertAuditLogAsync(entry);

        List<AuditLogEntry> entries = await repo.GetAuditLogEntriesForTenantAsync(
            tenantId, skip: 0, take: 10, actionFilter: null, fromDate: null, toDate: null);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].IpAddress).IsEqualTo("10.0.0.1");
    }

    // ========== Explicit entry IP wins over accessor ==========

    [Test]
    public async Task InsertAuditLogAsync_ExplicitEntryIp_NotOverriddenByAccessor()
    {
        using TestDatabaseFactory dbFactory = new();

        IAuditContextAccessor stubAccessor = Substitute.For<IAuditContextAccessor>();
        stubAccessor.GetClientIp().Returns("10.0.0.99");

        IAuditLogRepository repo = new DbRepo(
            dbFactory.Context,
            new NullLogger<DbRepo>(),
            stubAccessor);

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AuditLogEntry entry = new()
        {
            TenantId = tenantId,
            UserId = userId,
            MachineId = null,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            ResourceId = "user-1",
            Details = null,
            IpAddress = "192.168.5.5",
            Timestamp = DateTimeOffset.UtcNow,
        };

        await repo.InsertAuditLogAsync(entry);

        List<AuditLogEntry> entries = await repo.GetAuditLogEntriesForTenantAsync(
            tenantId, skip: 0, take: 10, actionFilter: null, fromDate: null, toDate: null);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].IpAddress).IsEqualTo("192.168.5.5");
    }

    // ========== Accessor returns null — entry IP stays null ==========

    [Test]
    public async Task InsertAuditLogAsync_AccessorReturnsNull_IpAddressRemainsNull()
    {
        using TestDatabaseFactory dbFactory = new();

        IAuditContextAccessor stubAccessor = Substitute.For<IAuditContextAccessor>();
        stubAccessor.GetClientIp().Returns((string?)null);

        IAuditLogRepository repo = new DbRepo(
            dbFactory.Context,
            new NullLogger<DbRepo>(),
            stubAccessor);

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AuditLogEntry entry = new()
        {
            TenantId = tenantId,
            UserId = userId,
            MachineId = null,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            ResourceId = "user-1",
            Details = null,
            IpAddress = null,
            Timestamp = DateTimeOffset.UtcNow,
        };

        await repo.InsertAuditLogAsync(entry);

        List<AuditLogEntry> entries = await repo.GetAuditLogEntriesForTenantAsync(
            tenantId, skip: 0, take: 10, actionFilter: null, fromDate: null, toDate: null);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].IpAddress).IsNull();
    }

    // ========== Default (no accessor arg) uses NullAuditContextAccessor ==========

    [Test]
    public async Task InsertAuditLogAsync_NoAccessorProvided_IpAddressRemainsNull()
    {
        using TestDatabaseFactory dbFactory = new();

        // Construct repository without providing an IAuditContextAccessor — the default
        // NullAuditContextAccessor should be used transparently.
        IAuditLogRepository repo = new DbRepo(
            dbFactory.Context,
            new NullLogger<DbRepo>());

        (int userId, int tenantId) = await SeedUserAndTenantAsync(dbFactory);

        AuditLogEntry entry = new()
        {
            TenantId = tenantId,
            UserId = userId,
            MachineId = null,
            Action = AuditAction.UserLogin,
            ResourceType = AuditResourceType.User,
            ResourceId = "user-1",
            Details = null,
            IpAddress = null,
            Timestamp = DateTimeOffset.UtcNow,
        };

        await repo.InsertAuditLogAsync(entry);

        List<AuditLogEntry> entries = await repo.GetAuditLogEntriesForTenantAsync(
            tenantId, skip: 0, take: 10, actionFilter: null, fromDate: null, toDate: null);

        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].IpAddress).IsNull();
    }
}
