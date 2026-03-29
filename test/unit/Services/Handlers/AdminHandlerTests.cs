// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web.Admin;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Users;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="AdminHandler"/>.
/// </summary>
public class AdminHandlerTests
{
    // ========== Constructor tests ==========

    [Test]
    public async Task Constructor_NullScopeFactory_ThrowsArgumentNullException()
    {
        await Assert.That(() =>
            new AdminHandler(null!))
            .Throws<ArgumentNullException>();
    }

    // ========== GetSettingsAsync tests ==========

    [Test]
    public async Task GetSettingsAsync_NoSettings_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        AdminHandler handler = new(dbFactory.Context);

        ServiceResult<List<SettingEntry>> result = await handler.GetSettingsAsync(CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetSettingsAsync_WithSettings_ReturnsMappedSettings()
    {
        using TestDatabaseFactory dbFactory = new();
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "300",
            Version = 1,
        });
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.OnlineThresholdSeconds,
            Value = "600",
            Version = 1,
        });

        AdminHandler handler = new(dbFactory.Context);

        ServiceResult<List<SettingEntry>> result = await handler.GetSettingsAsync(CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Count).IsEqualTo(2);
        await Assert.That(result.Data!.Any(e => e.Key == (int)ServerConfigurationSettingKeys.AgentHeartbeatSeconds && e.Value == "300")).IsEqualTo(true);
        await Assert.That(result.Data!.Any(e => e.Key == (int)ServerConfigurationSettingKeys.OnlineThresholdSeconds && e.Value == "600")).IsEqualTo(true);
    }

    // ========== GetAllUsersAsync tests ==========

    [Test]
    public async Task GetAllUsersAsync_NoUsers_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        AdminHandler handler = new(dbFactory.Context);

        ServiceResult<List<UserAccountDto>> result = await handler.GetAllUsersAsync(CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetAllUsersAsync_WithUsers_ReturnsAllUsers()
    {
        using TestDatabaseFactory dbFactory = new();
        UserAccount user1 = TestDataBuilder.BuildUser(username: "alice@example.com");
        UserAccount user2 = TestDataBuilder.BuildUser(username: "bob@example.com");
        user1.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user1);
        user2.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user2);

        AdminHandler handler = new(dbFactory.Context);

        ServiceResult<List<UserAccountDto>> result = await handler.GetAllUsersAsync(CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetAllUsersAsync_WithRoles_IncludesTenantRoles()
    {
        using TestDatabaseFactory dbFactory = new();
        Tenant tenant = TestDataBuilder.BuildTenant(name: "Test Corp");
        tenant.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        UserAccount user = TestDataBuilder.BuildUser(username: "admin@example.com");
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserTenantRole role = TestDataBuilder.BuildUserTenantRole(
            userId: user.Id,
            tenantId: tenant.Id,
            role: UserAccountRoles.TenantAdmin);
        await dbFactory.Context.InsertAsync(role);

        AdminHandler handler = new(dbFactory.Context);

        ServiceResult<List<UserAccountDto>> result = await handler.GetAllUsersAsync(CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Count).IsEqualTo(1);
        await Assert.That(result.Data![0].Tenants.Count).IsEqualTo(1);
        await Assert.That(result.Data![0].Tenants[0].TenantId).IsEqualTo(tenant.Id);
    }

    [Test]
    public async Task GetAllUsersAsync_InactiveRoles_ExcludedFromTenants()
    {
        using TestDatabaseFactory dbFactory = new();
        Tenant tenant = TestDataBuilder.BuildTenant(name: "Inactive Corp");
        tenant.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(tenant);

        UserAccount user = TestDataBuilder.BuildUser(username: "inactive@example.com");
        user.Id = await dbFactory.Context.InsertWithInt32IdentityAsync(user);

        UserTenantRole activeRole = TestDataBuilder.BuildUserTenantRole(
            userId: user.Id,
            tenantId: tenant.Id,
            role: UserAccountRoles.Viewer);
        await dbFactory.Context.InsertAsync(activeRole);

        UserTenantRole inactiveRole = TestDataBuilder.BuildUserTenantRole(
            userId: user.Id,
            tenantId: tenant.Id,
            role: UserAccountRoles.TenantAdmin);
        inactiveRole.IsActive = false;
        await dbFactory.Context.InsertAsync(inactiveRole);

        AdminHandler handler = new(dbFactory.Context);

        ServiceResult<List<UserAccountDto>> result = await handler.GetAllUsersAsync(CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data![0].Tenants.Count).IsEqualTo(1);
        await Assert.That(result.Data![0].Tenants[0].Role).IsEqualTo(((int)UserAccountRoles.Viewer).ToString());
    }
}
