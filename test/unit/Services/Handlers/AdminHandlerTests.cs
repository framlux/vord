// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web.Admin;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Users;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Handlers;

/// <summary>
/// Tests for <see cref="AdminHandler"/>.
/// </summary>
public class AdminHandlerTests
{
    // ========== GetSettingsAsync tests ==========

    [Test]
    public async Task GetSettingsAsync_NoSettings_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        AdminHandler handler = CreateHandler(dbFactory);

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

        AdminHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<SettingEntry>> result = await handler.GetSettingsAsync(CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Count).IsEqualTo(2);
        await Assert.That(result.Data!.Any(e => e.Key == (int)ServerConfigurationSettingKeys.AgentHeartbeatSeconds && e.Value == "300")).IsEqualTo(true);
        await Assert.That(result.Data!.Any(e => e.Key == (int)ServerConfigurationSettingKeys.OnlineThresholdSeconds && e.Value == "600")).IsEqualTo(true);
    }

    [Test]
    public async Task GetSettingsAsync_WithSettings_IncludesNameAndDescription()
    {
        using TestDatabaseFactory dbFactory = new();
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "300",
            Version = 1,
        });

        AdminHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<SettingEntry>> result = await handler.GetSettingsAsync(CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        SettingEntry entry = result.Data![0];
        await Assert.That(entry.Name).IsEqualTo("AgentHeartbeatSeconds");
        await Assert.That(entry.Description).IsNotEmpty();
    }

    // ========== UpdateSettingsAsync tests ==========

    [Test]
    public async Task UpdateSettingsAsync_ValidUpdate_UpdatesExistingRow()
    {
        using TestDatabaseFactory dbFactory = new();
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = CreateFakeRedis();
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "300",
            Version = 1,
        });

        AdminHandler handler = new(dbFactory.Context, cache, redis);
        List<SettingUpdateEntry> updates = [new() { Key = 1, Value = "600" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.First(e => e.Key == 1).Value).IsEqualTo("600");
        cache.Received(1).InvalidateCache();
    }

    [Test]
    public async Task UpdateSettingsAsync_ValidUpdate_InsertsNewRow()
    {
        using TestDatabaseFactory dbFactory = new();
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = CreateFakeRedis();

        AdminHandler handler = new(dbFactory.Context, cache, redis);
        List<SettingUpdateEntry> updates = [new() { Key = 1, Value = "500" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Count).IsEqualTo(1);
        await Assert.That(result.Data!.First(e => e.Key == 1).Value).IsEqualTo("500");
    }

    [Test]
    public async Task UpdateSettingsAsync_InvalidKey_ReturnsBadRequest()
    {
        using TestDatabaseFactory dbFactory = new();
        AdminHandler handler = CreateHandler(dbFactory);
        List<SettingUpdateEntry> updates = [new() { Key = 999, Value = "100" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(false);
        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).IsNotNull();
    }

    [Test]
    public async Task UpdateSettingsAsync_NoneKey_ReturnsBadRequest()
    {
        using TestDatabaseFactory dbFactory = new();
        AdminHandler handler = CreateHandler(dbFactory);
        List<SettingUpdateEntry> updates = [new() { Key = 0, Value = "100" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(false);
        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task UpdateSettingsAsync_EmptyValue_ReturnsBadRequest()
    {
        using TestDatabaseFactory dbFactory = new();
        AdminHandler handler = CreateHandler(dbFactory);
        List<SettingUpdateEntry> updates = [new() { Key = 1, Value = "" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(false);
        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task UpdateSettingsAsync_NegativeIntValue_ReturnsBadRequest()
    {
        using TestDatabaseFactory dbFactory = new();
        AdminHandler handler = CreateHandler(dbFactory);
        List<SettingUpdateEntry> updates = [new() { Key = 1, Value = "-5" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(false);
        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task UpdateSettingsAsync_NonNumericIntSetting_ReturnsBadRequest()
    {
        using TestDatabaseFactory dbFactory = new();
        AdminHandler handler = CreateHandler(dbFactory);
        List<SettingUpdateEntry> updates = [new() { Key = 1, Value = "abc" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(false);
        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task UpdateSettingsAsync_AllowUserSignup_AcceptsTrue()
    {
        using TestDatabaseFactory dbFactory = new();
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = CreateFakeRedis();

        AdminHandler handler = new(dbFactory.Context, cache, redis);
        List<SettingUpdateEntry> updates = [new() { Key = 8, Value = "true" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);

        // Verify the value was persisted to the database
        ServerConfigurationSettings? persisted = await dbFactory.Context.ServerConfigurationSettings
            .FirstOrDefaultAsync(s => s.Key == ServerConfigurationSettingKeys.AllowUserSignup);
        await Assert.That(persisted).IsNotNull();
        await Assert.That(persisted!.Value).IsEqualTo("true");
    }

    [Test]
    public async Task UpdateSettingsAsync_AllowUserSignup_AcceptsFalse()
    {
        using TestDatabaseFactory dbFactory = new();
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = CreateFakeRedis();

        AdminHandler handler = new(dbFactory.Context, cache, redis);
        List<SettingUpdateEntry> updates = [new() { Key = 8, Value = "false" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);

        // Verify the value was persisted to the database
        ServerConfigurationSettings? persisted = await dbFactory.Context.ServerConfigurationSettings
            .FirstOrDefaultAsync(s => s.Key == ServerConfigurationSettingKeys.AllowUserSignup);
        await Assert.That(persisted).IsNotNull();
        await Assert.That(persisted!.Value).IsEqualTo("false");
    }

    [Test]
    public async Task UpdateSettingsAsync_AllowUserSignup_RejectsInvalidValue()
    {
        using TestDatabaseFactory dbFactory = new();
        AdminHandler handler = CreateHandler(dbFactory);
        List<SettingUpdateEntry> updates = [new() { Key = 8, Value = "maybe" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(false);
        await Assert.That(result.StatusCode).IsEqualTo(400);
    }

    [Test]
    public async Task UpdateSettingsAsync_EmptyList_ReturnsCurrentSettings()
    {
        using TestDatabaseFactory dbFactory = new();
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = CreateFakeRedis();

        // Seed existing settings before calling with an empty update list
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "120",
            Version = 1,
        });
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.OnlineThresholdSeconds,
            Value = "300",
            Version = 1,
        });

        AdminHandler handler = new(dbFactory.Context, cache, redis);
        List<SettingUpdateEntry> updates = [];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Count).IsEqualTo(2);
        await Assert.That(result.Data!.Any(e => e.Key == (int)ServerConfigurationSettingKeys.AgentHeartbeatSeconds && e.Value == "120")).IsEqualTo(true);
        await Assert.That(result.Data!.Any(e => e.Key == (int)ServerConfigurationSettingKeys.OnlineThresholdSeconds && e.Value == "300")).IsEqualTo(true);
    }

    [Test]
    public async Task UpdateSettingsAsync_ValidUpdate_DeletesRedisKey()
    {
        using TestDatabaseFactory dbFactory = new();
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = CreateFakeRedis();
        IDatabase redisDb = redis.GetDatabase();

        AdminHandler handler = new(dbFactory.Context, cache, redis);
        List<SettingUpdateEntry> updates = [new() { Key = 1, Value = "600" }];

        await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await redisDb.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "config:AgentHeartbeatSeconds"),
            Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task UpdateSettingsAsync_ValidUpdate_IncrementsVersion()
    {
        using TestDatabaseFactory dbFactory = new();
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = CreateFakeRedis();
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "300",
            Version = 1,
        });

        AdminHandler handler = new(dbFactory.Context, cache, redis);
        List<SettingUpdateEntry> updates = [new() { Key = 1, Value = "600" }];

        await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        ServerConfigurationSettings? updated = await dbFactory.Context.ServerConfigurationSettings
            .FirstOrDefaultAsync(s => s.Key == ServerConfigurationSettingKeys.AgentHeartbeatSeconds);
        await Assert.That(updated).IsNotNull();
        await Assert.That(updated!.Version).IsEqualTo(2);
    }

    // ========== Bounds validation tests ==========

    [Test]
    public async Task UpdateSettingsAsync_HeartbeatBelowMin_ReturnsBadRequest()
    {
        using TestDatabaseFactory dbFactory = new();
        AdminHandler handler = CreateHandler(dbFactory);
        List<SettingUpdateEntry> updates = [new() { Key = 1, Value = "5" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(false);
        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).Contains("between 10 and 600");
    }

    [Test]
    public async Task UpdateSettingsAsync_HeartbeatAboveMax_ReturnsBadRequest()
    {
        using TestDatabaseFactory dbFactory = new();
        AdminHandler handler = CreateHandler(dbFactory);
        List<SettingUpdateEntry> updates = [new() { Key = 1, Value = "1000" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(false);
        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).Contains("between 10 and 600");
    }

    [Test]
    public async Task UpdateSettingsAsync_HeartbeatAtExactMin_Succeeds()
    {
        using TestDatabaseFactory dbFactory = new();
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = CreateFakeRedis();

        AdminHandler handler = new(dbFactory.Context, cache, redis);
        List<SettingUpdateEntry> updates = [new() { Key = 1, Value = "10" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
    }

    [Test]
    public async Task UpdateSettingsAsync_HeartbeatAtExactMax_Succeeds()
    {
        using TestDatabaseFactory dbFactory = new();
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = CreateFakeRedis();

        AdminHandler handler = new(dbFactory.Context, cache, redis);
        List<SettingUpdateEntry> updates = [new() { Key = 1, Value = "600" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
    }

    [Test]
    public async Task UpdateSettingsAsync_TelemetryCollectFastBelowMin_ReturnsBadRequest()
    {
        using TestDatabaseFactory dbFactory = new();
        AdminHandler handler = CreateHandler(dbFactory);
        List<SettingUpdateEntry> updates = [new() { Key = 9, Value = "3" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(false);
        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).Contains("between 10 and 300");
    }

    [Test]
    public async Task UpdateSettingsAsync_TelemetrySendFastAboveMax_ReturnsBadRequest()
    {
        using TestDatabaseFactory dbFactory = new();
        AdminHandler handler = CreateHandler(dbFactory);
        List<SettingUpdateEntry> updates = [new() { Key = 11, Value = "200" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(false);
        await Assert.That(result.StatusCode).IsEqualTo(400);
        await Assert.That(result.ErrorMessage).Contains("between 5 and 120");
    }

    [Test]
    public async Task UpdateSettingsAsync_TelemetrySettingsValidRange_Succeeds()
    {
        using TestDatabaseFactory dbFactory = new();
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = CreateFakeRedis();

        AdminHandler handler = new(dbFactory.Context, cache, redis);
        List<SettingUpdateEntry> updates =
        [
            new() { Key = 9, Value = "60" },
            new() { Key = 10, Value = "1800" },
            new() { Key = 11, Value = "10" },
            new() { Key = 12, Value = "600" },
        ];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data!.Count).IsEqualTo(4);
    }

    [Test]
    public async Task UpdateSettingsAsync_UnboundedSettingAcceptsLargeValue()
    {
        using TestDatabaseFactory dbFactory = new();
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = CreateFakeRedis();

        AdminHandler handler = new(dbFactory.Context, cache, redis);
        List<SettingUpdateEntry> updates = [new() { Key = 3, Value = "3600" }];

        ServiceResult<List<SettingEntry>> result = await handler.UpdateSettingsAsync(updates, CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
    }

    // ========== GetAllUsersAsync tests ==========

    [Test]
    public async Task GetAllUsersAsync_NoUsers_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        AdminHandler handler = CreateHandler(dbFactory);

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

        AdminHandler handler = CreateHandler(dbFactory);

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

        AdminHandler handler = CreateHandler(dbFactory);

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

        AdminHandler handler = CreateHandler(dbFactory);

        ServiceResult<List<UserAccountDto>> result = await handler.GetAllUsersAsync(CancellationToken.None);

        await Assert.That(result.IsSuccess).IsEqualTo(true);
        await Assert.That(result.Data![0].Tenants.Count).IsEqualTo(1);
        await Assert.That(result.Data![0].Tenants[0].Role).IsEqualTo(((int)UserAccountRoles.Viewer).ToString());
    }

    // ========== Helper methods ==========

    private static AdminHandler CreateHandler(TestDatabaseFactory dbFactory)
    {
        IServerSettingsCache cache = Substitute.For<IServerSettingsCache>();
        IConnectionMultiplexer redis = CreateFakeRedis();

        return new AdminHandler(dbFactory.Context, cache, redis);
    }

    private static IConnectionMultiplexer CreateFakeRedis()
    {
        IDatabase db = Substitute.For<IDatabase>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        return redis;
    }
}
