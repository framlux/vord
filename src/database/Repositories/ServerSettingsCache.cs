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
using StackExchange.Redis;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Singleton cache for server configuration settings.
/// Uses IServiceScopeFactory internally to create scoped DatabaseContext instances.
/// A 5-minute TTL is the correctness backstop; a Redis pub/sub subscription on
/// <see cref="InvalidationChannel"/> clears individual entries promptly across replicas when a setting
/// changes. Pub/sub is best-effort — if the subscription cannot be established the TTL still bounds
/// staleness.
/// </summary>
public sealed class ServerSettingsCache : IServerSettingsCache
{
    /// <summary>
    /// Redis pub/sub channel carrying per-key settings invalidations. The message body is the integer
    /// setting key; on receipt the named entry is cleared from the in-memory cache.
    /// </summary>
    public const string InvalidationChannel = "config:invalidate";

    private static readonly long SettingsCacheTtlTicks = TimeSpan.FromMinutes(5).Ticks;

    private readonly ConcurrentDictionary<ServerConfigurationSettingKeys, ServerConfigurationSettings> _cache = [];
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<ServerSettingsCache> _logger;
    private readonly TimeProvider _timeProvider;
    private long _cacheRefreshedAtTicks = DateTimeOffset.MinValue.Ticks;

    /// <summary>
    /// Creates a new instance of the <see cref="ServerSettingsCache"/> class.
    /// </summary>
    /// <param name="serviceScopeFactory">Factory used to create DI scopes for database access</param>
    /// <param name="logger">Internal structured logger</param>
    /// <param name="timeProvider">Time provider used for TTL expiry calculations</param>
    /// <param name="redis">Redis connection used to subscribe to cross-replica invalidations.</param>
    public ServerSettingsCache(IServiceScopeFactory serviceScopeFactory, ILogger<ServerSettingsCache> logger, TimeProvider timeProvider, IConnectionMultiplexer redis)
    {
        _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentNullException.ThrowIfNull(redis);

        SubscribeToInvalidations(redis);
    }

    private void SubscribeToInvalidations(IConnectionMultiplexer redis)
    {
        try
        {
            redis.GetSubscriber().Subscribe(
                RedisChannel.Literal(InvalidationChannel),
                (channel, message) =>
                {
                    if (int.TryParse(message.ToString(), out int rawKey) &&
                        Enum.IsDefined((ServerConfigurationSettingKeys)rawKey))
                    {
                        _cache.TryRemove((ServerConfigurationSettingKeys)rawKey, out ServerConfigurationSettings? _);
                    }
                });
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            // Best-effort: without the subscription the 5-minute TTL still bounds staleness.
            _logger.LogWarning(ex, "Could not subscribe to settings invalidation channel; relying on the cache TTL");
        }
    }

    /// <inheritdoc/>
    public async Task<string?> GetSettingAsync(ServerConfigurationSettingKeys key, CancellationToken cancellationToken)
    {
        // Expire the settings cache after the TTL so DB changes propagate without restart.
        // Uses Interlocked.CompareExchange to avoid a TOCTOU race between check and clear.
        long nowTicks = _timeProvider.GetUtcNow().Ticks;
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
    public async Task<string?> GetSettingFromDatabaseAsync(ServerConfigurationSettingKeys key, CancellationToken cancellationToken = default)
    {
        // Deliberately bypasses the in-memory cache: the shared Redis read-through repopulation must
        // never seed Redis from a possibly-stale local cache, or a just-cleared Redis key could be
        // re-filled with the old value on another replica.
        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        DatabaseContext dbContext = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        ServerConfigurationSettings? configSetting = await dbContext.ServerConfigurationSettings
            .Where(s => s.Key == key)
            .OrderByDescending(s => s.Version)
            .FirstOrDefaultAsync(cancellationToken);

        return string.IsNullOrEmpty(configSetting?.Value) ? null : configSetting.Value;
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
