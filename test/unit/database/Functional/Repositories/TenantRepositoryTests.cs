// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Functional.DatabaseRepository;

/// <summary>
/// Functional tests for tenant-related methods on <see cref="Database.Repositories.DatabaseRepository"/>.
/// </summary>
public class TenantCacheTests
{
    [Test]
    public async Task CreateTenantAsync_ValidTenant_ReturnsTenantWithId()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

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
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

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
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

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
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Tenant? result = await cache.GetTenantByIdAsync(99999);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetTenantByExternalIdAsync_ExistingTenant_ReturnsTenant()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

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
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

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
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Tenant? result = await cache.GetTenantByExternalIdAsync("does-not-exist");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetTenantByNameAsync_ExactMatch_ReturnsTenant()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

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
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

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
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

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
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        Tenant? result = await cache.GetTenantByNameAsync("No Such Tenant");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task CreateUserTenantRoleAsync_ValidRole_PersistsToDatabase()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

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
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

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
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        IEnumerable<UserTenantRole> result = await cache.GetTenantsForUserAsync("nonexistent-external-id");

        await Assert.That(result.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task GetTenantsForUserByIdAsync_UserWithMultipleTenants_ReturnsAll()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant1 = TestDataBuilder.BuildTenant(name: "By-Id Tenant A", createdByUserId: userId);
        int tenantId1 = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant1);

        Tenant tenant2 = TestDataBuilder.BuildTenant(name: "By-Id Tenant B", createdByUserId: userId);
        int tenantId2 = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant2);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: userId, tenantId: tenantId1, assignedByUserId: userId));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: userId, tenantId: tenantId2, assignedByUserId: userId));

        IEnumerable<UserTenantRole> result = await cache.GetTenantsForUserByIdAsync(userId);

        await Assert.That(result.Count()).IsEqualTo(2);
    }

    [Test]
    public async Task GetTenantsForUserByIdAsync_NoRoles_ReturnsEmpty()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        IEnumerable<UserTenantRole> result = await cache.GetTenantsForUserByIdAsync(99999);

        await Assert.That(result.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task GetTenantsForUserByIdAsync_TwoUsersShareExternalId_ReturnsOnlyTargetUsersRoles()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        // Two accounts that collide on ExternalId but belong to different providers. A lookup keyed
        // on ExternalId alone would leak the victim's roles into the attacker's claims; the by-id
        // lookup must return only the roles belonging to the requested account.
        UserAccount victim = TestDataBuilder.BuildUser(externalId: "shared-external-id");
        victim.AuthProvider = AuthProviderType.Google;
        int victimId = await dbFactory.Context.InsertWithInt32IdentityAsync(victim);

        UserAccount attacker = TestDataBuilder.BuildUser(externalId: "shared-external-id");
        attacker.AuthProvider = AuthProviderType.GitHub;
        int attackerId = await dbFactory.Context.InsertWithInt32IdentityAsync(attacker);

        Tenant victimTenant = TestDataBuilder.BuildTenant(name: "Victim Tenant", createdByUserId: victimId);
        int victimTenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(victimTenant);

        Tenant attackerTenant = TestDataBuilder.BuildTenant(name: "Attacker Tenant", createdByUserId: attackerId);
        int attackerTenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(attackerTenant);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: victimId, tenantId: victimTenantId, assignedByUserId: victimId));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: attackerId, tenantId: attackerTenantId, assignedByUserId: attackerId));

        IEnumerable<UserTenantRole> attackerRoles = await cache.GetTenantsForUserByIdAsync(attackerId);

        List<UserTenantRole> rolesList = attackerRoles.ToList();
        await Assert.That(rolesList.Count).IsEqualTo(1);
        await Assert.That(rolesList[0].AssignedTenantId).IsEqualTo(attackerTenantId);
    }

    [Test]
    public async Task GetMembersForTenantAsync_ActiveMembers_ReturnsOnlyActive()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

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
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

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

        await Assert.That(result).IsTrue();

        // Verify the user no longer appears in members
        IEnumerable<UserTenantRole> members = await cache.GetMembersForTenantAsync(tenantId);

        await Assert.That(members.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task DisableUserTenantRoleAsync_NoActiveRole_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        bool result = await cache.DisableUserTenantRoleAsync(9999, 9999, 1);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task GetActiveUserRoleAsync_ActiveRole_ReturnsRole()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildUser());
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant(createdByUserId: userId));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: userId, tenantId: tenantId, role: UserAccountRoles.MachineAdmin, assignedByUserId: userId));

        UserAccountRoles? role = await cache.GetActiveUserRoleAsync(userId, tenantId, CancellationToken.None);

        await Assert.That(role).IsEqualTo(UserAccountRoles.MachineAdmin);
    }

    [Test]
    public async Task GetActiveUserRoleAsync_RoleInDifferentTenant_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildUser());
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant(createdByUserId: userId));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: userId, tenantId: tenantId, role: UserAccountRoles.TenantAdmin, assignedByUserId: userId));

        // Querying a different tenant the user is not a member of returns null.
        UserAccountRoles? role = await cache.GetActiveUserRoleAsync(userId, tenantId + 1000, CancellationToken.None);

        await Assert.That(role).IsNull();
    }

    [Test]
    public async Task GetActiveUserRoleAsync_InactiveRole_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildUser());
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(TestDataBuilder.BuildTenant(createdByUserId: userId));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: userId, tenantId: tenantId, role: UserAccountRoles.TenantAdmin, assignedByUserId: userId, isActive: false));

        UserAccountRoles? role = await cache.GetActiveUserRoleAsync(userId, tenantId, CancellationToken.None);

        await Assert.That(role).IsNull();
    }

    [Test]
    public async Task GetTenantAdminEmails_ReturnsActiveTenantAdminsOnly()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        // Seed tenant 1
        UserAccount adminUser = TestDataBuilder.BuildUser(username: "admin@x.com");
        int adminUserId = await dbFactory.Context.InsertWithInt32IdentityAsync(adminUser);

        UserAccount viewerUser = TestDataBuilder.BuildUser(username: "viewer@x.com");
        int viewerUserId = await dbFactory.Context.InsertWithInt32IdentityAsync(viewerUser);

        UserAccount inactiveAdminUser = TestDataBuilder.BuildUser(username: "old@x.com", isActive: false);
        int inactiveAdminUserId = await dbFactory.Context.InsertWithInt32IdentityAsync(inactiveAdminUser);

        Tenant tenant1 = TestDataBuilder.BuildTenant(name: "Tenant One");
        int tenantId1 = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant1);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(
            userId: adminUserId, tenantId: tenantId1, role: UserAccountRoles.TenantAdmin, assignedByUserId: adminUserId));

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(
            userId: viewerUserId, tenantId: tenantId1, role: UserAccountRoles.Viewer, assignedByUserId: adminUserId));

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(
            userId: inactiveAdminUserId, tenantId: tenantId1, role: UserAccountRoles.TenantAdmin, assignedByUserId: adminUserId));

        // Seed a second tenant with its own admin — must not appear in tenant 1 results
        UserAccount otherTenantAdmin = TestDataBuilder.BuildUser(username: "otheradmin@x.com");
        int otherTenantAdminId = await dbFactory.Context.InsertWithInt32IdentityAsync(otherTenantAdmin);

        Tenant tenant2 = TestDataBuilder.BuildTenant(name: "Tenant Two");
        int tenantId2 = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant2);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(
            userId: otherTenantAdminId, tenantId: tenantId2, role: UserAccountRoles.TenantAdmin, assignedByUserId: otherTenantAdminId));

        List<string> emails = await cache.GetTenantAdminEmailsAsync(tenantId1);

        await Assert.That(emails).Contains("admin@x.com");
        await Assert.That(emails).DoesNotContain("viewer@x.com");
        await Assert.That(emails).DoesNotContain("old@x.com");
        await Assert.That(emails).DoesNotContain("otheradmin@x.com");
    }

    [Test]
    public async Task GetTenantAdminEmails_ExcludesAdminWithInactiveRoleRow()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount activeAdmin = TestDataBuilder.BuildUser(username: "active@x.com");
        int activeAdminId = await dbFactory.Context.InsertWithInt32IdentityAsync(activeAdmin);

        // Active account but inactive TenantAdmin role row — must not appear in results.
        UserAccount revokedAdmin = TestDataBuilder.BuildUser(username: "revoked@x.com");
        int revokedAdminId = await dbFactory.Context.InsertWithInt32IdentityAsync(revokedAdmin);

        Tenant tenant = TestDataBuilder.BuildTenant(name: "Role Filter Tenant");
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(
            userId: activeAdminId, tenantId: tenantId, role: UserAccountRoles.TenantAdmin, assignedByUserId: activeAdminId));

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(
            userId: revokedAdminId, tenantId: tenantId, role: UserAccountRoles.TenantAdmin, assignedByUserId: activeAdminId,
            isActive: false));

        List<string> emails = await cache.GetTenantAdminEmailsAsync(tenantId);

        await Assert.That(emails).Contains("active@x.com");
        await Assert.That(emails).DoesNotContain("revoked@x.com");
    }

    [Test]
    public async Task GetTenantAdminEmails_ReturnsEmpty_WhenNoAdminsExist()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser(username: "viewer@nonadmin.com");
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant = TestDataBuilder.BuildTenant(name: "No Admin Tenant", createdByUserId: userId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(
            userId: userId, tenantId: tenantId, role: UserAccountRoles.Viewer, assignedByUserId: userId));

        List<string> emails = await cache.GetTenantAdminEmailsAsync(tenantId);

        await Assert.That(emails.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CountActiveMembersAsync_ActiveAndInactiveRoles_CountsOnlyActive()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user1 = TestDataBuilder.BuildUser();
        int userId1 = await dbFactory.Context.InsertWithInt32IdentityAsync(user1);

        UserAccount user2 = TestDataBuilder.BuildUser();
        int userId2 = await dbFactory.Context.InsertWithInt32IdentityAsync(user2);

        UserAccount user3 = TestDataBuilder.BuildUser();
        int userId3 = await dbFactory.Context.InsertWithInt32IdentityAsync(user3);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: userId1);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Two active roles
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(
            userId: userId1, tenantId: tenantId, assignedByUserId: userId1, isActive: true));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(
            userId: userId2, tenantId: tenantId, assignedByUserId: userId1, isActive: true));

        // One inactive role
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(
            userId: userId3, tenantId: tenantId, assignedByUserId: userId1, isActive: false));

        int count = await cache.CountActiveMembersAsync(tenantId, CancellationToken.None);

        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task CountActiveMembersAsync_OtherTenantRoles_ExcludesOtherTenant()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount user = TestDataBuilder.BuildUser();
        int userId = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        Tenant tenant1 = TestDataBuilder.BuildTenant(name: "Count Tenant Alpha", createdByUserId: userId);
        int tenantId1 = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant1);

        Tenant tenant2 = TestDataBuilder.BuildTenant(name: "Count Tenant Beta", createdByUserId: userId);
        int tenantId2 = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant2);

        // One active role in tenant1
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(
            userId: userId, tenantId: tenantId1, assignedByUserId: userId, isActive: true));

        // One active role in tenant2 — must not affect tenant1's count
        UserAccount user2 = TestDataBuilder.BuildUser();
        int userId2 = await dbFactory.Context.InsertWithInt32IdentityAsync(user2);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(
            userId: userId2, tenantId: tenantId2, assignedByUserId: userId2, isActive: true));

        int count = await cache.CountActiveMembersAsync(tenantId1, CancellationToken.None);

        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task CreateUserTenantRoleWithMemberLimit_AtLimit_ReturnsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount existingUser = TestDataBuilder.BuildUser();
        int existingUserId = await dbFactory.Context.InsertWithInt32IdentityAsync(existingUser);

        UserAccount newUser = TestDataBuilder.BuildUser();
        int newUserId = await dbFactory.Context.InsertWithInt32IdentityAsync(newUser);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: existingUserId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // One active member already occupies the only seat.
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(
            userId: existingUserId, tenantId: tenantId, assignedByUserId: existingUserId, isActive: true));

        UserTenantRole role = TestDataBuilder.BuildUserTenantRole(
            userId: newUserId, tenantId: tenantId, assignedByUserId: existingUserId);

        bool added = await cache.CreateUserTenantRoleWithMemberLimitAsync(role, memberLimit: 1, CancellationToken.None);

        await Assert.That(added).IsFalse();

        // The insert must have been rejected, leaving the member count unchanged.
        int count = await cache.CountActiveMembersAsync(tenantId, CancellationToken.None);
        await Assert.That(count).IsEqualTo(1);
    }

    [Test]
    public async Task CreateUserTenantRoleWithMemberLimit_UnderLimit_InsertsAndReturnsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount existingUser = TestDataBuilder.BuildUser();
        int existingUserId = await dbFactory.Context.InsertWithInt32IdentityAsync(existingUser);

        UserAccount newUser = TestDataBuilder.BuildUser();
        int newUserId = await dbFactory.Context.InsertWithInt32IdentityAsync(newUser);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: existingUserId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(
            userId: existingUserId, tenantId: tenantId, assignedByUserId: existingUserId, isActive: true));

        UserTenantRole role = TestDataBuilder.BuildUserTenantRole(
            userId: newUserId, tenantId: tenantId, assignedByUserId: existingUserId);

        bool added = await cache.CreateUserTenantRoleWithMemberLimitAsync(role, memberLimit: 5, CancellationToken.None);

        await Assert.That(added).IsTrue();

        int count = await cache.CountActiveMembersAsync(tenantId, CancellationToken.None);
        await Assert.That(count).IsEqualTo(2);
    }

    [Test]
    public async Task CreateUserTenantRoleWithMemberLimit_NullLimit_AlwaysInserts()
    {
        using TestDatabaseFactory dbFactory = new();
        ITenantRepository cache = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        UserAccount existingUser = TestDataBuilder.BuildUser();
        int existingUserId = await dbFactory.Context.InsertWithInt32IdentityAsync(existingUser);

        UserAccount newUser = TestDataBuilder.BuildUser();
        int newUserId = await dbFactory.Context.InsertWithInt32IdentityAsync(newUser);

        Tenant tenant = TestDataBuilder.BuildTenant(createdByUserId: existingUserId);
        int tenantId = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        // Seed several existing members; a null limit must not reject regardless of count.
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(
            userId: existingUserId, tenantId: tenantId, assignedByUserId: existingUserId, isActive: true));

        UserTenantRole role = TestDataBuilder.BuildUserTenantRole(
            userId: newUserId, tenantId: tenantId, assignedByUserId: existingUserId);

        bool added = await cache.CreateUserTenantRoleWithMemberLimitAsync(role, memberLimit: null, CancellationToken.None);

        await Assert.That(added).IsTrue();

        int count = await cache.CountActiveMembersAsync(tenantId, CancellationToken.None);
        await Assert.That(count).IsEqualTo(2);
    }
}
