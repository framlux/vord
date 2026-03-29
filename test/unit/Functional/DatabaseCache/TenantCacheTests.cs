// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseCache;

/// <summary>
/// Functional tests for tenant-related methods on <see cref="Database.Cache.DatabaseCache"/>.
/// </summary>
public class TenantCacheTests
{
    [Test]
    public async Task CreateTenantAsync_ValidTenant_ReturnsTenantWithId()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(name: "New Tenant", createdByUserId: userId);

        Tenant result = await cache.CreateTenantAsync(tenant);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsNotEqualTo(0);
        // CreateTenantAsync normalizes tenant name to lowercase.
        await Assert.That(result.Name).IsEqualTo("new tenant");
    }

    [Test]
    public async Task GetTenantByIdAsync_ExistingActiveTenant_ReturnsTenant()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(name: "Lookup Tenant", createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        Tenant? result = await cache.GetTenantByIdAsync(tenantId);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Id).IsEqualTo(tenantId);
        await Assert.That(result.Name).IsEqualTo("Lookup Tenant");
    }

    [Test]
    public async Task GetTenantByIdAsync_InactiveTenant_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        tenant.IsActive = false;
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        Tenant? result = await cache.GetTenantByIdAsync(tenantId);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetTenantByIdAsync_NonExistentId_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        Tenant? result = await cache.GetTenantByIdAsync(99999);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetTenantByExternalIdAsync_ExistingTenant_ReturnsTenant()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(externalId: "ext-tenant-lookup", createdByUserId: userId);
        await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        Tenant? result = await cache.GetTenantByExternalIdAsync("ext-tenant-lookup");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.ExternalId).IsEqualTo("ext-tenant-lookup");
    }

    [Test]
    public async Task GetTenantByExternalIdAsync_InactiveTenant_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(externalId: "ext-inactive-tenant", createdByUserId: userId);
        tenant.IsActive = false;
        await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        Tenant? result = await cache.GetTenantByExternalIdAsync("ext-inactive-tenant");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetTenantByExternalIdAsync_NonExistentExternalId_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        Tenant? result = await cache.GetTenantByExternalIdAsync("does-not-exist");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetTenantByNameAsync_ExactMatch_ReturnsTenant()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        // Names are normalized to lowercase at write time.
        Tenant tenant = TestDataBuilder.BuildTenant(name: "exact name tenant", createdByUserId: userId);
        await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        Tenant? result = await cache.GetTenantByNameAsync("Exact Name Tenant");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("exact name tenant");
    }

    [Test]
    public async Task GetTenantByNameAsync_CaseInsensitiveMatch_ReturnsTenant()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        // Names are normalized to lowercase at write time.
        Tenant tenant = TestDataBuilder.BuildTenant(name: "case test tenant", createdByUserId: userId);
        await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Query with mixed case — GetTenantByNameAsync normalizes input.
        Tenant? result = await cache.GetTenantByNameAsync("Case Test Tenant");

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Name).IsEqualTo("case test tenant");
    }

    [Test]
    public async Task GetTenantByNameAsync_InactiveTenant_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(name: "Inactive Named Tenant", createdByUserId: userId);
        tenant.IsActive = false;
        await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        Tenant? result = await cache.GetTenantByNameAsync("Inactive Named Tenant");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetTenantByNameAsync_NonExistentName_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        Tenant? result = await cache.GetTenantByNameAsync("No Such Tenant");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task CreateUserTenantRoleAsync_ValidRole_PersistsToDatabase()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        UserTenantRole role = TestDataBuilder.BuildUserTenantRole(
            userId: userId,
            tenantId: tenantId,
            role: UserAccountRoles.TenantAdmin,
            assignedByUserId: userId);

        await cache.CreateUserTenantRoleAsync(role);

        // Verify role was persisted by querying the members
        IEnumerable<UserTenantRole> members = await cache.GetMembersForTenantAsync(tenantId);

        await Assert.That(members.Count()).IsEqualTo(1);
        await Assert.That(members.First().UserId).IsEqualTo(userId);
        await Assert.That(members.First().Role).IsEqualTo(UserAccountRoles.TenantAdmin);
    }

    [Test]
    public async Task GetTenantsForUserAsync_UserWithMultipleTenants_ReturnsAll()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount user = TestDataBuilder.BuildUser(externalId: "ext-multi-tenant-user");
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant1 = TestDataBuilder.BuildTenant(name: "Tenant A", createdByUserId: userId);
        int tenantId1 = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant1);

        Tenant tenant2 = TestDataBuilder.BuildTenant(name: "Tenant B", createdByUserId: userId);
        int tenantId2 = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant2);

        UserTenantRole role1 = TestDataBuilder.BuildUserTenantRole(userId: userId, tenantId: tenantId1, assignedByUserId: userId);
        await dbFactory.Context.InsertAsync(role1);

        UserTenantRole role2 = TestDataBuilder.BuildUserTenantRole(userId: userId, tenantId: tenantId2, assignedByUserId: userId);
        await dbFactory.Context.InsertAsync(role2);

        IEnumerable<UserTenantRole> result = await cache.GetTenantsForUserAsync("ext-multi-tenant-user");

        await Assert.That(result.Count()).IsEqualTo(2);
    }

    [Test]
    public async Task GetTenantsForUserAsync_NoRoles_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        IEnumerable<UserTenantRole> result = await cache.GetTenantsForUserAsync("nonexistent-external-id");

        await Assert.That(result.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task GetMembersForTenantAsync_ActiveMembers_ReturnsOnlyActive()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount activeUser = TestDataBuilder.BuildUser();
        int activeUserId = await dbFactory.Context.InsertWithInt32IdentityAsync(activeUser);

        UserAccount inactiveUser = TestDataBuilder.BuildUser(isActive: false);
        int inactiveUserId = await dbFactory.Context.InsertWithInt32IdentityAsync(inactiveUser);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: activeUserId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        UserTenantRole activeRole = TestDataBuilder.BuildUserTenantRole(
            userId: activeUserId, tenantId: tenantId, assignedByUserId: activeUserId);
        await dbFactory.Context.InsertAsync(activeRole);

        UserTenantRole inactiveRole = TestDataBuilder.BuildUserTenantRole(
            userId: inactiveUserId, tenantId: tenantId, assignedByUserId: activeUserId);
        await dbFactory.Context.InsertAsync(inactiveRole);

        IEnumerable<UserTenantRole> result = await cache.GetMembersForTenantAsync(tenantId);

        await Assert.That(result.Count()).IsEqualTo(1);
        await Assert.That(result.First().UserId).IsEqualTo(activeUserId);
    }

    [Test]
    public async Task DisableUserTenantRoleAsync_ActiveRole_ReturnsTrueAndDisables()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserAccount admin = TestDataBuilder.BuildUser();
        int adminId = await dbFactory.Context.InsertWithInt32IdentityAsync(admin);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        UserTenantRole role = TestDataBuilder.BuildUserTenantRole(
            userId: userId, tenantId: tenantId, assignedByUserId: userId);
        await dbFactory.Context.InsertAsync(role);

        bool result = await cache.DisableUserTenantRoleAsync(userId, tenantId, adminId);

        await Assert.That(result).IsEqualTo(true);

        // Verify the user no longer appears in members
        IEnumerable<UserTenantRole> members = await cache.GetMembersForTenantAsync(tenantId);

        await Assert.That(members.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task DisableUserTenantRoleAsync_NoActiveRole_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        IDatabaseCache cache = new Database.Cache.DatabaseCache(dbFactory.Context, new NullLogger<Database.Cache.DatabaseCache>());

        bool result = await cache.DisableUserTenantRoleAsync(9999, 9999, 1);

        await Assert.That(result).IsEqualTo(false);
    }
}
