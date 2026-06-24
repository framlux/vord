// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Test.Infrastructure;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging.Abstractions;

namespace Framlux.FleetManagement.Test.Repositories;

/// <summary>
/// Tests for the string-keyed <see cref="ServerSettingsCache.GetSettingAsync(string, System.Threading.CancellationToken)"/>
/// and <see cref="ServerSettingsCache.SetSettingAsync(string, string, System.Threading.CancellationToken)"/> methods,
/// which back the per-shard streaming high-water marks. The harness drives a real in-memory SQLite
/// database through a test scope factory, mirroring the enum-keyed cache tests.
/// </summary>
public class ServerSettingsCacheStringKeyTests
{
    private const string StringKey = "streaming.hwm:shard:0";

    private static ServerSettingsCache CreateCache(TestDatabaseFactory dbFactory)
    {
        TestServiceScopeFactory scopeFactory = new(dbFactory.Context);

        return new ServerSettingsCache(scopeFactory, NullLogger<ServerSettingsCache>.Instance);
    }

    [Test]
    public async Task GetSettingAsync_StringKey_NoRow_ReturnsNull()
    {
        using TestDatabaseFactory dbFactory = new();
        ServerSettingsCache cache = CreateCache(dbFactory);

        string? result = await cache.GetSettingAsync(StringKey, CancellationToken.None);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetSettingAsync_StringKey_SecondCall_ServedFromCacheWithoutDbRoundTrip()
    {
        using TestDatabaseFactory dbFactory = new();
        ServerSettingsCache cache = CreateCache(dbFactory);

        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.None,
            StringKey = StringKey,
            Value = "42",
            Version = 1,
        });

        // First read populates the cache from the database.
        string? first = await cache.GetSettingAsync(StringKey, CancellationToken.None);
        await Assert.That(first).IsEqualTo("42");

        // Remove the row underneath the cache. A second read that still returns the value proves it
        // was served from the in-memory cache and did not perform a second database round-trip.
        await dbFactory.Context.ServerConfigurationSettings
            .Where(s => s.StringKey == StringKey)
            .DeleteAsync();

        string? second = await cache.GetSettingAsync(StringKey, CancellationToken.None);

        await Assert.That(second).IsEqualTo("42");
    }

    [Test]
    public async Task SetSettingAsync_StringKey_FirstWrite_InsertsVersionOne()
    {
        using TestDatabaseFactory dbFactory = new();
        ServerSettingsCache cache = CreateCache(dbFactory);

        await cache.SetSettingAsync(StringKey, "7", CancellationToken.None);

        ServerConfigurationSettings? row = await dbFactory.Context.ServerConfigurationSettings
            .Where(s => s.StringKey == StringKey)
            .FirstOrDefaultAsync();

        await Assert.That(row).IsNotNull();
        await Assert.That(row!.Value).IsEqualTo("7");
        await Assert.That(row.Version).IsEqualTo(1);
        await Assert.That(row.Key).IsEqualTo(ServerConfigurationSettingKeys.None);
    }

    [Test]
    public async Task SetSettingAsync_StringKey_SecondWrite_UpdatesAndBumpsVersion()
    {
        using TestDatabaseFactory dbFactory = new();
        ServerSettingsCache cache = CreateCache(dbFactory);

        await cache.SetSettingAsync(StringKey, "7", CancellationToken.None);
        await cache.SetSettingAsync(StringKey, "9", CancellationToken.None);

        List<ServerConfigurationSettings> rows = await dbFactory.Context.ServerConfigurationSettings
            .Where(s => s.StringKey == StringKey)
            .ToListAsync();

        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Value).IsEqualTo("9");
        await Assert.That(rows[0].Version).IsEqualTo(2);
    }

    [Test]
    public async Task SetSettingAsync_StringKey_ThenGet_ReturnsWrittenValueFromCache()
    {
        using TestDatabaseFactory dbFactory = new();
        ServerSettingsCache cache = CreateCache(dbFactory);

        await cache.SetSettingAsync(StringKey, "100", CancellationToken.None);

        string? result = await cache.GetSettingAsync(StringKey, CancellationToken.None);

        await Assert.That(result).IsEqualTo("100");
    }

    [Test]
    public async Task GetSettingAsync_StringKey_AfterInvalidate_RereadsFromDatabase()
    {
        using TestDatabaseFactory dbFactory = new();
        ServerSettingsCache cache = CreateCache(dbFactory);

        await dbFactory.Context.InsertAsync(new ServerConfigurationSettings
        {
            Key = ServerConfigurationSettingKeys.None,
            StringKey = StringKey,
            Value = "1",
            Version = 1,
        });

        await Assert.That(await cache.GetSettingAsync(StringKey, CancellationToken.None)).IsEqualTo("1");

        // Change the stored value, then invalidate the cache so the next read goes back to the DB.
        await dbFactory.Context.ServerConfigurationSettings
            .Where(s => s.StringKey == StringKey)
            .Set(s => s.Value, "2")
            .UpdateAsync();
        cache.InvalidateCache();

        string? reread = await cache.GetSettingAsync(StringKey, CancellationToken.None);

        await Assert.That(reread).IsEqualTo("2");
    }

    [Test]
    public async Task GetSettingAsync_EmptyStringKey_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        ServerSettingsCache cache = CreateCache(dbFactory);

        await Assert.That(() => cache.GetSettingAsync(string.Empty, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task GetSettingAsync_NullStringKey_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        ServerSettingsCache cache = CreateCache(dbFactory);

        await Assert.That(() => cache.GetSettingAsync(null!, CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task SetSettingAsync_EmptyStringKey_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        ServerSettingsCache cache = CreateCache(dbFactory);

        await Assert.That(() => cache.SetSettingAsync(string.Empty, "x", CancellationToken.None))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task SetSettingAsync_NullStringKey_ThrowsArgumentException()
    {
        using TestDatabaseFactory dbFactory = new();
        ServerSettingsCache cache = CreateCache(dbFactory);

        await Assert.That(() => cache.SetSettingAsync(null!, "x", CancellationToken.None))
            .Throws<ArgumentException>();
    }
}
