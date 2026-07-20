// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Repositories;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.ServerConfiguration;

/// <summary>
/// Provides typed access to server configuration settings with built-in defaults.
/// Uses Redis as a shared cache layer so all server replicas see config changes promptly.
/// Falls back to the database when Redis cache misses.
/// </summary>
public sealed class ServerConfigurationService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly IServerSettingsCache _cache;
    private readonly IConnectionMultiplexer _redis;

    /// <summary>
    /// Creates a new instance of the <see cref="ServerConfigurationService"/> class.
    /// </summary>
    /// <param name="cache">The server settings cache for reading configuration settings.</param>
    /// <param name="redis">The Redis connection multiplexer for shared caching.</param>
    public ServerConfigurationService(IServerSettingsCache cache, IConnectionMultiplexer redis)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
    }

    /// <summary>
    /// Gets the agent heartbeat interval in seconds.
    /// </summary>
    public async Task<int> GetAgentHeartbeatSecondsAsync(CancellationToken ct = default)
    {
        return await GetIntSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, ServerSettingDefaults.AgentHeartbeatSeconds, ct);
    }

    /// <summary>
    /// Gets the agent configuration refresh interval in seconds.
    /// </summary>
    public async Task<int> GetAgentConfigRefreshSecondsAsync(CancellationToken ct = default)
    {
        return await GetIntSettingAsync(ServerConfigurationSettingKeys.AgentConfigRefreshSeconds, ServerSettingDefaults.AgentConfigRefreshSeconds, ct);
    }

    /// <summary>
    /// Gets the online threshold as a TimeSpan.
    /// </summary>
    public async Task<TimeSpan> GetOnlineThresholdAsync(CancellationToken ct = default)
    {
        int seconds = await GetIntSettingAsync(ServerConfigurationSettingKeys.OnlineThresholdSeconds, ServerSettingDefaults.OnlineThresholdSeconds, ct);

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Gets the deduplication TTL as a TimeSpan.
    /// </summary>
    public async Task<TimeSpan> GetDeduplicationTtlAsync(CancellationToken ct = default)
    {
        int seconds = await GetIntSettingAsync(ServerConfigurationSettingKeys.DeduplicationTtlSeconds, ServerSettingDefaults.DeduplicationTtlSeconds, ct);

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Gets the agent command poll interval in seconds.
    /// </summary>
    public async Task<int> GetAgentCommandPollSecondsAsync(CancellationToken ct = default)
    {
        return await GetIntSettingAsync(ServerConfigurationSettingKeys.AgentCommandPollSeconds, ServerSettingDefaults.AgentCommandPollSeconds, ct);
    }

    /// <summary>
    /// Gets the fast telemetry collection interval in seconds.
    /// </summary>
    public async Task<int> GetTelemetryCollectFastSecondsAsync(CancellationToken ct = default)
    {
        return await GetIntSettingAsync(ServerConfigurationSettingKeys.TelemetryCollectFastSeconds, ServerSettingDefaults.TelemetryCollectFastSeconds, ct);
    }

    /// <summary>
    /// Gets the slow telemetry collection interval in seconds.
    /// </summary>
    public async Task<int> GetTelemetryCollectSlowSecondsAsync(CancellationToken ct = default)
    {
        return await GetIntSettingAsync(ServerConfigurationSettingKeys.TelemetryCollectSlowSeconds, ServerSettingDefaults.TelemetryCollectSlowSeconds, ct);
    }

    /// <summary>
    /// Gets the fast telemetry send interval in seconds.
    /// </summary>
    public async Task<int> GetTelemetrySendFastSecondsAsync(CancellationToken ct = default)
    {
        return await GetIntSettingAsync(ServerConfigurationSettingKeys.TelemetrySendFastSeconds, ServerSettingDefaults.TelemetrySendFastSeconds, ct);
    }

    /// <summary>
    /// Gets the slow telemetry send interval in seconds.
    /// </summary>
    public async Task<int> GetTelemetrySendSlowSecondsAsync(CancellationToken ct = default)
    {
        return await GetIntSettingAsync(ServerConfigurationSettingKeys.TelemetrySendSlowSeconds, ServerSettingDefaults.TelemetrySendSlowSeconds, ct);
    }

    /// <summary>
    /// Gets the service status collection interval in seconds.
    /// </summary>
    public async Task<int> GetServiceStatusSecondsAsync(CancellationToken ct = default)
    {
        return await GetIntSettingAsync(ServerConfigurationSettingKeys.ServiceStatusSeconds, ServerSettingDefaults.ServiceStatusSeconds, ct);
    }

    /// <summary>
    /// Gets whether new users may self-register via social login. Read through the shared Redis cache
    /// so a flip on any replica is visible to all others promptly (immediately on invalidation, else
    /// within the Redis TTL) rather than each replica honoring its own stale in-memory copy. Defaults
    /// to allowed unless the setting is explicitly "false".
    /// </summary>
    public async Task<bool> GetAllowUserSignupAsync(CancellationToken ct = default)
    {
        string? value = await GetStringSettingAsync(ServerConfigurationSettingKeys.AllowUserSignup, ct);

        return string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) == false;
    }

    private async Task<int> GetIntSettingAsync(ServerConfigurationSettingKeys key, int defaultValue, CancellationToken ct)
    {
        string redisKey = $"config:{key}";
        IDatabase db = _redis.GetDatabase();

        // Try Redis first (shared across all replicas).
        RedisValue cached = await db.StringGetAsync(redisKey);
        if (cached.HasValue && int.TryParse(cached.ToString(), out int cachedValue) && cachedValue > 0)
        {
            return cachedValue;
        }

        // Fall back to the database directly — never the local cache — so a just-invalidated Redis key
        // is not re-seeded from another replica's stale in-memory value.
        string? value = await _cache.GetSettingFromDatabaseAsync(key, ct);
        if (value is not null && int.TryParse(value, out int parsed) && parsed > 0)
        {
            // Store in Redis so other replicas can read it.
            await db.StringSetAsync(redisKey, parsed.ToString(), CacheTtl);

            return parsed;
        }

        return defaultValue;
    }

    private async Task<string?> GetStringSettingAsync(ServerConfigurationSettingKeys key, CancellationToken ct)
    {
        string redisKey = $"config:{key}";
        IDatabase db = _redis.GetDatabase();

        RedisValue cached = await db.StringGetAsync(redisKey);
        if (cached.HasValue)
        {
            return cached.ToString();
        }

        // Authoritative read from the database (not the local cache) so a cleared Redis key is not
        // re-seeded stale, then repopulate the shared cache for other replicas.
        string? value = await _cache.GetSettingFromDatabaseAsync(key, ct);
        if (value is not null)
        {
            await db.StringSetAsync(redisKey, value, CacheTtl);
        }

        return value;
    }
}
