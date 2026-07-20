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
/// Functional tests for server-configuration-related methods on <see cref="Database.Repositories.DatabaseRepository"/>.
/// </summary>
public class ServerConfigurationRepositoryTests
{
    // ========== ListAllSettingsAsync tests ==========



    // ========== ListAllSettingsAsync tests ==========

    [Test]
    public async Task ListAllSettingsAsync_MultipleSettings_ReturnsOrderedByKey()
    {
        using TestDatabaseFactory dbFactory = new();
        IServerConfigurationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        // Insert in non-key order to verify sorting
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.OnlineThresholdSeconds,
            Value = "600",
            Version = 1,
        });
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "300",
            Version = 1,
        });
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.DeduplicationTtlSeconds,
            Value = "120",
            Version = 1,
        });

        List<ServerConfigurationSettings> result = await repo.ListAllSettingsAsync();

        await Assert.That(result.Count).IsEqualTo(3);
        // AgentHeartbeatSeconds (1) < OnlineThresholdSeconds (3) < DeduplicationTtlSeconds (6)
        await Assert.That(result[0].Key).IsEqualTo(ServerConfigurationSettingKeys.AgentHeartbeatSeconds);
        await Assert.That(result[1].Key).IsEqualTo(ServerConfigurationSettingKeys.OnlineThresholdSeconds);
        await Assert.That(result[2].Key).IsEqualTo(ServerConfigurationSettingKeys.DeduplicationTtlSeconds);
    }

    [Test]
    public async Task ListAllSettingsAsync_NoSettings_ReturnsEmptyList()
    {
        using TestDatabaseFactory dbFactory = new();
        IServerConfigurationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        List<ServerConfigurationSettings> result = await repo.ListAllSettingsAsync();

        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== UpsertSettingAsync tests ==========

    [Test]
    public async Task UpsertSettingAsync_NewKey_InsertsNewSetting()
    {
        using TestDatabaseFactory dbFactory = new();
        IServerConfigurationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await repo.UpsertSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, "300");

        List<ServerConfigurationSettings> allSettings = await repo.ListAllSettingsAsync();

        await Assert.That(allSettings.Count).IsEqualTo(1);
        await Assert.That(allSettings[0].Key).IsEqualTo(ServerConfigurationSettingKeys.AgentHeartbeatSeconds);
        await Assert.That(allSettings[0].Value).IsEqualTo("300");
        await Assert.That(allSettings[0].Version).IsEqualTo(1);
    }

    [Test]
    public async Task UpsertSettingAsync_ExistingKey_UpdatesValueAndIncrementsVersion()
    {
        using TestDatabaseFactory dbFactory = new();
        IServerConfigurationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await repo.UpsertSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, "300");
        await repo.UpsertSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, "600");

        List<ServerConfigurationSettings> allSettings = await repo.ListAllSettingsAsync();

        await Assert.That(allSettings.Count).IsEqualTo(1);
        await Assert.That(allSettings[0].Value).IsEqualTo("600");
        await Assert.That(allSettings[0].Version).IsEqualTo(2);
    }

    [Test]
    public async Task ListAllSettingsAsync_RowsForRemovedEnumKeys_AreFilteredOut()
    {
        using TestDatabaseFactory dbFactory = new();
        IServerConfigurationRepository repo = new Database.Repositories.DatabaseRepository(dbFactory.Context, new NullLogger<Database.Repositories.DatabaseRepository>());

        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "300",
            Version = 1,
        });

        // Deployed databases can contain rows whose keys were later removed from the enum
        // (keys 4 and 5 were seeded by early migrations). They must not surface in the list.
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = (ServerConfigurationSettingKeys)4,
            Value = "30",
            Version = 1,
        });

        List<ServerConfigurationSettings> result = await repo.ListAllSettingsAsync();

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Key).IsEqualTo(ServerConfigurationSettingKeys.AgentHeartbeatSeconds);
    }
}
