// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Repositories;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.ServerConfiguration;

/// <summary>
/// Shared helper for evicting the shared Redis read-through entry for a setting and fanning out a
/// per-key invalidation to every replica's in-memory <c>ServerSettingsCache</c> via Redis pub/sub.
/// Best-effort: a Redis connectivity failure is logged and swallowed because the 5-minute cache TTL
/// is the correctness backstop.
/// </summary>
public static class ServerSettingsInvalidation
{
    /// <summary>
    /// Deletes the shared Redis read-through entry for <paramref name="key"/> and publishes an
    /// invalidation for it. Call only after the database write has committed.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="key">The setting key whose caches should be evicted.</param>
    /// <param name="logger">Logger used to warn when the fan-out could not be delivered.</param>
    public static async Task PublishAsync(IConnectionMultiplexer redis, ServerConfigurationSettingKeys key, ILogger logger)
    {
        try
        {
            IDatabase db = redis.GetDatabase();
            await db.KeyDeleteAsync($"config:{key}");
            await redis.GetSubscriber().PublishAsync(RedisChannel.Literal(ServerSettingsCache.InvalidationChannel), ((int)key).ToString());
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            logger.LogWarning(ex, "Failed to fan out settings invalidation for {Key}; replicas will refresh on the cache TTL", key);
        }
    }
}
