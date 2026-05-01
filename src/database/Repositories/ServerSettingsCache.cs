// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Collections.Concurrent;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Singleton cache for server configuration settings.
/// Uses IServiceScopeFactory internally to create scoped DatabaseContext instances.
/// </summary>
public sealed class ServerSettingsCache : IServerSettingsCache
{
    private static readonly long SettingsCacheTtlTicks = TimeSpan.FromMinutes(5).Ticks;

    private readonly ConcurrentDictionary<ServerConfigurationSettingKeys, ServerConfigurationSettings> _cache = [];
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<ServerSettingsCache> _logger;
    private long _cacheRefreshedAtTicks = DateTimeOffset.MinValue.Ticks;

    /// <summary>
    /// Creates a new instance of the <see cref="ServerSettingsCache"/> class.
    /// </summary>
    /// <param name="serviceScopeFactory">Factory used to create DI scopes for database access</param>
    /// <param name="logger">Internal structured logger</param>
    public ServerSettingsCache(IServiceScopeFactory serviceScopeFactory, ILogger<ServerSettingsCache> logger)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task<string?> GetSettingAsync(ServerConfigurationSettingKeys key, CancellationToken cancellationToken)
    {
        // Expire the settings cache after the TTL so DB changes propagate without restart.
        // Uses Interlocked.CompareExchange to avoid TOCTOU race between check and clear.
        long nowTicks = DateTimeOffset.UtcNow.Ticks;
        long lastRefreshTicks = Interlocked.Read(ref _cacheRefreshedAtTicks);
        if (((nowTicks - lastRefreshTicks) > SettingsCacheTtlTicks) &&
            (Interlocked.CompareExchange(ref _cacheRefreshedAtTicks, nowTicks, lastRefreshTicks) == lastRefreshTicks))
        {
            _cache.Clear();
        }

        if (_cache.TryGetValue(key, out ServerConfigurationSettings? setting))
        {
            return setting.Value;
        }

        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        DatabaseContext dbContext = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        ServerConfigurationSettings? configSetting = await dbContext.ServerConfigurationSettings
            .Where(s => s.Key == key)
            .OrderByDescending(s => s.Version)
            .FirstOrDefaultAsync(cancellationToken);

        if ((configSetting is null) || string.IsNullOrEmpty(configSetting.Value))
        {
            return null;
        }

        if (_cache.TryAdd(key, configSetting) == false)
        {
            _logger.LogWarning("Failed to add {Key} to cache", key);
        }

        return configSetting.Value;
    }

    /// <inheritdoc/>
    public async Task SetSettingAsync(ServerConfigurationSettingKeys key, string value, CancellationToken cancellationToken)
    {
        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        DatabaseContext dbContext = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        ServerConfigurationSettings? existing = await dbContext.ServerConfigurationSettings
            .Where(s => s.Key == key)
            .OrderByDescending(s => s.Version)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is null)
        {
            await dbContext.InsertAsync(new ServerConfigurationSettings
            {
                Key = key,
                Value = value,
                Version = 1,
            }, token: cancellationToken);
        }
        else
        {
            await dbContext.ServerConfigurationSettings
                .Where(s => s.Id == existing.Id)
                .Set(s => s.Value, value)
                .Set(s => s.Version, existing.Version + 1)
                .UpdateAsync(cancellationToken);
        }

        // Update the in-memory cache entry.
        ServerConfigurationSettings cached = new ServerConfigurationSettings
        {
            Key = key,
            Value = value,
            Version = (existing?.Version ?? 0) + 1,
        };
        _cache.AddOrUpdate(key, cached, (_, _) => cached);
    }

    /// <inheritdoc/>
    public void InvalidateCache()
    {
        _cache.Clear();
        Interlocked.Exchange(ref _cacheRefreshedAtTicks, DateTimeOffset.MinValue.Ticks);
    }
}
