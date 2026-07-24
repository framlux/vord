// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Repositories;

/// <summary>
/// Behavioral tests for <see cref="ServerSettingsReader"/>. The reader is deliberately
/// uncached — the shared Redis read-through repopulates from it, so every call must reflect
/// the current database state.
/// </summary>
public sealed class ServerSettingsReaderTests
{
    /// <summary>
    /// A stored setting is returned as written.
    /// </summary>
    [Test]
    public async Task GetSettingFromDatabaseAsync_ExistingSetting_ReturnsValue()
    {
        using TestDatabaseFactory dbFactory = new();
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "30",
            Version = 1,
        });

        ServerSettingsReader reader = new(CreateScopeFactory(dbFactory.Context));

        string? value = await reader.GetSettingFromDatabaseAsync(
            ServerConfigurationSettingKeys.AgentHeartbeatSeconds, CancellationToken.None);

        await Assert.That(value).IsEqualTo("30");
    }

    /// <summary>
    /// A change written after an earlier read is visible immediately: the reader holds no
    /// state of its own, which is what lets the Redis layer own invalidation entirely.
    /// </summary>
    [Test]
    public async Task GetSettingFromDatabaseAsync_ValueChanged_ReturnsNewValueImmediately()
    {
        using TestDatabaseFactory dbFactory = new();
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "30",
            Version = 1,
        });

        ServerSettingsReader reader = new(CreateScopeFactory(dbFactory.Context));

        await Assert.That(await reader.GetSettingFromDatabaseAsync(
            ServerConfigurationSettingKeys.AgentHeartbeatSeconds, CancellationToken.None)).IsEqualTo("30");

        await dbFactory.Context.ServerConfigurationSettings
            .Where(s => s.Key == ServerConfigurationSettingKeys.AgentHeartbeatSeconds)
            .Set(s => s.Value, "99")
            .UpdateAsync();

        await Assert.That(await reader.GetSettingFromDatabaseAsync(
            ServerConfigurationSettingKeys.AgentHeartbeatSeconds, CancellationToken.None)).IsEqualTo("99");
    }

    /// <summary>
    /// When several versions of a setting exist the highest version wins.
    /// </summary>
    [Test]
    public async Task GetSettingFromDatabaseAsync_MultipleVersions_ReturnsHighestVersion()
    {
        using TestDatabaseFactory dbFactory = new();
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "30",
            Version = 1,
        });
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "45",
            Version = 2,
        });

        ServerSettingsReader reader = new(CreateScopeFactory(dbFactory.Context));

        string? value = await reader.GetSettingFromDatabaseAsync(
            ServerConfigurationSettingKeys.AgentHeartbeatSeconds, CancellationToken.None);

        await Assert.That(value).IsEqualTo("45");
    }

    /// <summary>
    /// An absent setting reads as null so callers apply their built-in default.
    /// </summary>
    [Test]
    public async Task GetSettingFromDatabaseAsync_MissingSetting_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        ServerSettingsReader reader = new(CreateScopeFactory(dbFactory.Context));

        string? value = await reader.GetSettingFromDatabaseAsync(
            ServerConfigurationSettingKeys.AgentHeartbeatSeconds, CancellationToken.None);

        await Assert.That(value).IsNull();
    }

    /// <summary>
    /// A stored-but-empty value is treated as unset rather than surfacing an empty string that
    /// would fail every downstream parse.
    /// </summary>
    [Test]
    public async Task GetSettingFromDatabaseAsync_EmptyValue_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "",
            Version = 1,
        });

        ServerSettingsReader reader = new(CreateScopeFactory(dbFactory.Context));

        string? value = await reader.GetSettingFromDatabaseAsync(
            ServerConfigurationSettingKeys.AgentHeartbeatSeconds, CancellationToken.None);

        await Assert.That(value).IsNull();
    }

    /// <summary>
    /// Constructor must reject a null <see cref="IServiceScopeFactory"/> so a misconfigured DI
    /// composition fails fast at startup rather than silently at runtime.
    /// </summary>
    [Test]
    public async Task Constructor_NullScopeFactory_ThrowsArgumentNullException()
    {
        await Assert.That(() => new ServerSettingsReader(null!)).Throws<ArgumentNullException>();
    }

    private static IServiceScopeFactory CreateScopeFactory(DatabaseContext context)
    {
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider.GetService(typeof(DatabaseContext)).Returns(context);

        IServiceScope scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(serviceProvider);

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);

        return scopeFactory;
    }
}
