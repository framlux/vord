// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.ServerConfiguration;

/// <summary>
/// Shared helper for evicting the shared Redis read-through entry for a setting. Best-effort: a
/// Redis connectivity failure is logged and swallowed because the Redis entry's own TTL is the
/// correctness backstop.
/// </summary>
public static class ServerSettingsInvalidation
{
    /// <summary>
    /// Deletes the shared Redis read-through entry for <paramref name="key"/> so the next read on
    /// any replica falls through to the database. Call only after the database write has committed.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="key">The setting key whose cached entry should be evicted.</param>
    /// <param name="logger">Logger used to warn when the eviction could not be delivered.</param>
    public static async Task InvalidateAsync(IConnectionMultiplexer redis, ServerConfigurationSettingKeys key, ILogger logger)
    {
        try
        {
            IDatabase db = redis.GetDatabase();
            await db.KeyDeleteAsync($"config:{key}");
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            logger.LogWarning(ex, "Failed to evict the cached setting {Key}; replicas will refresh on the cache TTL", key);
        }
    }
}
