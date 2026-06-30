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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Repositories;

/// <summary>
/// Behavioral tests for <see cref="ServerSettingsCache"/> TTL expiry driven by an injected
/// <see cref="TimeProvider"/>. Verifies that the cache serves the first-read value until
/// the 5-minute TTL elapses, then discards it and re-queries the database.
/// </summary>
public sealed class ServerSettingsCacheTests
{
    /// <summary>
    /// A value read before the TTL elapses must return the originally cached value, not a
    /// subsequently-updated database value.
    /// </summary>
    [Test]
    public async Task GetSettingAsync_BeforeTtlElapses_ReturnsCachedValue()
    {
        using TestDatabaseFactory dbFactory = new();
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "30",
            Version = 1,
        });

        FakeTimeProvider timeProvider = new(DateTimeOffset.UtcNow);
        IServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory.Context);
        ServerSettingsCache cache = new(scopeFactory, NullLogger<ServerSettingsCache>.Instance, timeProvider);

        // First read — populates the in-memory cache.
        string? firstRead = await cache.GetSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, CancellationToken.None);
        await Assert.That(firstRead).IsEqualTo("30");

        // Update the database row while the cache is still warm.
        await dbFactory.Context.ServerConfigurationSettings
            .Where(s => s.Key == ServerConfigurationSettingKeys.AgentHeartbeatSeconds)
            .Set(s => s.Value, "99")
            .UpdateAsync();

        // Advance time to just under the 5-minute TTL — cache should still hold the old value.
        timeProvider.Advance(TimeSpan.FromMinutes(4).Add(TimeSpan.FromSeconds(59)));

        string? secondRead = await cache.GetSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, CancellationToken.None);

        await Assert.That(secondRead).IsEqualTo("30");
    }

    /// <summary>
    /// A value read after the TTL elapses must discard the cache entry and return the
    /// current database value, proving that the TTL boundary is respected.
    /// </summary>
    [Test]
    public async Task GetSettingAsync_AfterTtlElapses_ReturnsUpdatedDatabaseValue()
    {
        using TestDatabaseFactory dbFactory = new();
        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.AgentHeartbeatSeconds,
            Value = "30",
            Version = 1,
        });

        FakeTimeProvider timeProvider = new(DateTimeOffset.UtcNow);
        IServiceScopeFactory scopeFactory = CreateScopeFactory(dbFactory.Context);
        ServerSettingsCache cache = new(scopeFactory, NullLogger<ServerSettingsCache>.Instance, timeProvider);

        // Populate the cache.
        string? firstRead = await cache.GetSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, CancellationToken.None);
        await Assert.That(firstRead).IsEqualTo("30");

        // Update the database row.
        await dbFactory.Context.ServerConfigurationSettings
            .Where(s => s.Key == ServerConfigurationSettingKeys.AgentHeartbeatSeconds)
            .Set(s => s.Value, "99")
            .UpdateAsync();

        // Advance time past the 5-minute TTL so the cache expires.
        timeProvider.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));

        // The expired cache must be discarded and the new database value returned.
        string? postTtlRead = await cache.GetSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, CancellationToken.None);

        await Assert.That(postTtlRead).IsEqualTo("99");
    }

    /// <summary>
    /// Constructor must reject a null <see cref="TimeProvider"/> argument so misconfigured
    /// DI composition fails fast at startup rather than silently at runtime.
    /// </summary>
    [Test]
    public async Task Constructor_NullTimeProvider_ThrowsArgumentNullException()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();

        await Assert.That(() =>
            new ServerSettingsCache(scopeFactory, NullLogger<ServerSettingsCache>.Instance, null!))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Constructor must reject a null <see cref="IServiceScopeFactory"/> argument.
    /// </summary>
    [Test]
    public async Task Constructor_NullScopeFactory_ThrowsArgumentNullException()
    {
        await Assert.That(() =>
            new ServerSettingsCache(null!, NullLogger<ServerSettingsCache>.Instance, TimeProvider.System))
            .Throws<ArgumentNullException>();
    }

    /// <summary>
    /// Constructor must reject a null logger argument.
    /// </summary>
    [Test]
    public async Task Constructor_NullLogger_ThrowsArgumentNullException()
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();

        await Assert.That(() =>
            new ServerSettingsCache(scopeFactory, null!, TimeProvider.System))
            .Throws<ArgumentNullException>();
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
