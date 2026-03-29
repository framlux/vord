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

namespace Framlux.FleetManagement.Database.Cache;

/// <summary>
/// Singleton cache for server configuration settings.
/// Uses IServiceScopeFactory internally to create scoped DatabaseContext instances.
/// </summary>
public sealed class ServerSettingsCache : IServerSettingsCache
{
    private static readonly TimeSpan SettingsCacheTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<ServerConfigurationSettingKeys, ServerConfigurationSettings> _cache = [];
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<ServerSettingsCache> _logger;
    private DateTimeOffset _cacheRefreshedAt = DateTimeOffset.MinValue;

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
        // Capture timestamp once to avoid TOCTOU race between check and clear.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _cacheRefreshedAt > SettingsCacheTtl)
        {
            _cacheRefreshedAt = now;
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
    public void InvalidateCache()
    {
        _cache.Clear();
        _cacheRefreshedAt = DateTimeOffset.MinValue;
    }
}
