// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Models.Users;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="UserHandler"/>.
/// </summary>
public class UserHandlerTests
{
    private static async Task<(int userId, int tenantId)> SeedUserWithRole(
        TestDatabaseFactory dbFactory,
        string? username = null,
        UserAccountRoles role = UserAccountRoles.Viewer,
        bool isSystem = false,
        bool isActive = true,
        bool roleActive = true)
    {
        Tenant tenant = TestDataBuilder.BuildTenant();
        tenant.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        UserAccount user = TestDataBuilder.BuildUser(username: username);
        user.IsSystem = isSystem;
        user.IsActive = isActive;
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserTenantRole utr = TestDataBuilder.BuildUserTenantRole(
            userId: user.Id,
            tenantId: tenant.Id,
            role: role);
        utr.IsActive = roleActive;
        await dbFactory.Context.InsertAsync(utr);

        return (user.Id, tenant.Id);
    }

    // ========== ListAsync tests ==========

    [Test]
    public async Task ListAsync_NullTenantId_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        ILogger<UserHandler> logger = Substitute.For<ILogger<UserHandler>>();
        UserHandler handler = new(CreateRepo(dbFactory), logger);

        ServiceResult<List<UserAccountDto>> result = await handler.ListAsync(null, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ListAsync_NoUsersInTenant_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        ILogger<UserHandler> logger = Substitute.For<ILogger<UserHandler>>();
        UserHandler handler = new(CreateRepo(dbFactory), logger);

        ServiceResult<List<UserAccountDto>> result = await handler.ListAsync(999, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ListAsync_WithUsers_ReturnsUsersWithRoles()
    {
        using TestDatabaseFactory dbFactory = new();
        (int userId, int tenantId) = await SeedUserWithRole(dbFactory, username: "viewer@example.com");

        ILogger<UserHandler> logger = Substitute.For<ILogger<UserHandler>>();
        UserHandler handler = new(CreateRepo(dbFactory), logger);

        ServiceResult<List<UserAccountDto>> result = await handler.ListAsync(tenantId, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Count).IsEqualTo(1);
        await Assert.That(result.Data![0].Username).IsEqualTo("viewer@example.com");
        await Assert.That(result.Data![0].Tenants.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ListAsync_ExcludesSystemUsers()
    {
        using TestDatabaseFactory dbFactory = new();
        (_, int tenantId) = await SeedUserWithRole(dbFactory, username: "system@example.com", isSystem: true);
        // Add a non-system user to same tenant
        UserAccount normalUser = TestDataBuilder.BuildUser(username: "normal@example.com");
        normalUser.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(normalUser);
        UserTenantRole normalRole = TestDataBuilder.BuildUserTenantRole(userId: normalUser.Id, tenantId: tenantId);
        await dbFactory.Context.InsertAsync(normalRole);

        ILogger<UserHandler> logger = Substitute.For<ILogger<UserHandler>>();
        UserHandler handler = new(CreateRepo(dbFactory), logger);

        ServiceResult<List<UserAccountDto>> result = await handler.ListAsync(tenantId, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Count).IsEqualTo(1);
        await Assert.That(result.Data![0].Username).IsEqualTo("normal@example.com");
    }

    [Test]
    public async Task ListAsync_OnlyReturnsUsersWithActiveRolesInTenant()
    {
        using TestDatabaseFactory dbFactory = new();
        (_, int tenantId) = await SeedUserWithRole(dbFactory, username: "inactive@example.com", roleActive: false);

        ILogger<UserHandler> logger = Substitute.For<ILogger<UserHandler>>();
        UserHandler handler = new(CreateRepo(dbFactory), logger);

        ServiceResult<List<UserAccountDto>> result = await handler.ListAsync(tenantId, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Count).IsEqualTo(0);
    }

    // ========== GetDetailAsync tests ==========

    [Test]
    public async Task GetDetailAsync_NullTenantId_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        ILogger<UserHandler> logger = Substitute.For<ILogger<UserHandler>>();
        UserHandler handler = new(CreateRepo(dbFactory), logger);

        ServiceResult<UserAccountDto> result = await handler.GetDetailAsync(1, null, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetDetailAsync_UserNotInTenant_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        (int userId, _) = await SeedUserWithRole(dbFactory);

        ILogger<UserHandler> logger = Substitute.For<ILogger<UserHandler>>();
        UserHandler handler = new(CreateRepo(dbFactory), logger);

        ServiceResult<UserAccountDto> result = await handler.GetDetailAsync(userId, 999, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetDetailAsync_UserNotFound_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        ILogger<UserHandler> logger = Substitute.For<ILogger<UserHandler>>();
        UserHandler handler = new(CreateRepo(dbFactory), logger);

        ServiceResult<UserAccountDto> result = await handler.GetDetailAsync(999, 1, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task GetDetailAsync_ValidUser_ReturnsUserWithTenantRoles()
    {
        using TestDatabaseFactory dbFactory = new();
        (int userId, int tenantId) = await SeedUserWithRole(dbFactory, username: "detail@example.com", role: UserAccountRoles.TenantAdmin);

        ILogger<UserHandler> logger = Substitute.For<ILogger<UserHandler>>();
        UserHandler handler = new(CreateRepo(dbFactory), logger);

        ServiceResult<UserAccountDto> result = await handler.GetDetailAsync(userId, tenantId, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Data!.Username).IsEqualTo("detail@example.com");
        await Assert.That(result.Data!.Tenants.Count).IsEqualTo(1);
    }

    // ========== DeactivateAsync tests ==========

    [Test]
    public async Task DeactivateAsync_SelfDeactivation_Returns400()
    {
        using TestDatabaseFactory dbFactory = new();
        ILogger<UserHandler> logger = Substitute.For<ILogger<UserHandler>>();
        UserHandler handler = new(CreateRepo(dbFactory), logger);

        ServiceResult<object> result = await handler.DeactivateAsync(5, 5, 1, CancellationToken.None);

        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task DeactivateAsync_NullTenantId_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        ILogger<UserHandler> logger = Substitute.For<ILogger<UserHandler>>();
        UserHandler handler = new(CreateRepo(dbFactory), logger);

        ServiceResult<object> result = await handler.DeactivateAsync(1, 2, null, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task DeactivateAsync_UserNotInTenant_ReturnsNotFound()
    {
        using TestDatabaseFactory dbFactory = new();
        (int userId, _) = await SeedUserWithRole(dbFactory);

        ILogger<UserHandler> logger = Substitute.For<ILogger<UserHandler>>();
        UserHandler handler = new(CreateRepo(dbFactory), logger);

        ServiceResult<object> result = await handler.DeactivateAsync(userId, userId + 1, 999, CancellationToken.None);

        await Assert.That(result.IsNotFound).IsTrue();
    }

    [Test]
    public async Task DeactivateAsync_LastActiveRole_DeactivatesAccount()
    {
        using TestDatabaseFactory dbFactory = new();
        (int userId, int tenantId) = await SeedUserWithRole(dbFactory, username: "lastactive@example.com");

        // Create admin user who performs the deactivation
        UserAccount admin = TestDataBuilder.BuildUser(username: "admin@example.com");
        admin.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(admin);

        ILogger<UserHandler> logger = Substitute.For<ILogger<UserHandler>>();
        UserHandler handler = new(CreateRepo(dbFactory), logger);

        ServiceResult<object> result = await handler.DeactivateAsync(userId, admin.Id, tenantId, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();

        // Verify role deactivated
        UserTenantRole? role = await dbFactory.Context.UserTenantRoles
            .FirstOrDefaultAsync(r => r.UserId == userId && r.AssignedTenantId == tenantId);
        await Assert.That(role!.IsActive).IsFalse();

        // Verify account deactivated (last active role)
        UserAccount? user = await dbFactory.Context.UserAccounts.FirstOrDefaultAsync(u => u.Id == userId);
        await Assert.That(user!.IsActive).IsFalse();
    }

    [Test]
    public async Task DeactivateAsync_OtherActiveRoles_OnlyDeactivatesRole()
    {
        using TestDatabaseFactory dbFactory = new();
        // Create tenant 1 and tenant 2
        Tenant tenant1 = TestDataBuilder.BuildTenant(name: "Tenant One");
        tenant1.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant1);
        Tenant tenant2 = TestDataBuilder.BuildTenant(name: "Tenant Two");
        tenant2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant2);

        // Create user with roles in both tenants
        UserAccount user = TestDataBuilder.BuildUser(username: "multi@example.com");
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: user.Id, tenantId: tenant1.Id));
        await dbFactory.Context.InsertAsync(TestDataBuilder.BuildUserTenantRole(userId: user.Id, tenantId: tenant2.Id));

        // Create admin user
        UserAccount admin = TestDataBuilder.BuildUser(username: "admin2@example.com");
        admin.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(admin);

        ILogger<UserHandler> logger = Substitute.For<ILogger<UserHandler>>();
        UserHandler handler = new(CreateRepo(dbFactory), logger);

        // Deactivate from tenant 1 only
        ServiceResult<object> result = await handler.DeactivateAsync(user.Id, admin.Id, tenant1.Id, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsTrue();

        // Verify role in tenant 1 deactivated
        UserTenantRole? role1 = await dbFactory.Context.UserTenantRoles
            .FirstOrDefaultAsync(r => r.UserId == user.Id && r.AssignedTenantId == tenant1.Id);
        await Assert.That(role1!.IsActive).IsFalse();

        // Verify role in tenant 2 still active
        UserTenantRole? role2 = await dbFactory.Context.UserTenantRoles
            .FirstOrDefaultAsync(r => r.UserId == user.Id && r.AssignedTenantId == tenant2.Id);
        await Assert.That(role2!.IsActive).IsTrue();

        // Verify account still active (has other active roles)
        UserAccount? userAfter = await dbFactory.Context.UserAccounts.FirstOrDefaultAsync(u => u.Id == user.Id);
        await Assert.That(userAfter!.IsActive).IsTrue();
    }

    // ========== Helper methods ==========

    private static DatabaseRepository CreateRepo(TestDatabaseFactory dbFactory)
    {
        return new DatabaseRepository(dbFactory.Context, new NullLogger<DatabaseRepository>());
    }
}
