// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Services.ServerConfiguration;

/// <summary>
/// Provides typed access to server configuration settings with built-in defaults.
/// Uses Redis as a shared cache layer so all server replicas see config changes promptly.
/// Falls back to the database when Redis cache misses.
/// </summary>
public sealed class ServerConfigurationService
{
    private const int DefaultAgentHeartbeatSeconds = 300;
    private const int DefaultAgentConfigRefreshSeconds = 900;
    private const int DefaultOnlineThresholdSeconds = 300;
    private const int DefaultDeduplicationTtlSeconds = 300;
    private const int DefaultAgentCommandPollSeconds = 30;
    private const int DefaultTelemetryCollectFastSeconds = 30;
    private const int DefaultTelemetryCollectSlowSeconds = 900;
    private const int DefaultTelemetrySendFastSeconds = 15;
    private const int DefaultTelemetrySendSlowSeconds = 300;
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
        return await GetIntSettingAsync(ServerConfigurationSettingKeys.AgentHeartbeatSeconds, DefaultAgentHeartbeatSeconds, ct);
    }

    /// <summary>
    /// Gets the agent configuration refresh interval in seconds.
    /// </summary>
    public async Task<int> GetAgentConfigRefreshSecondsAsync(CancellationToken ct = default)
    {
        return await GetIntSettingAsync(ServerConfigurationSettingKeys.AgentConfigRefreshSeconds, DefaultAgentConfigRefreshSeconds, ct);
    }

    /// <summary>
    /// Gets the online threshold as a TimeSpan.
    /// </summary>
    public async Task<TimeSpan> GetOnlineThresholdAsync(CancellationToken ct = default)
    {
        int seconds = await GetIntSettingAsync(ServerConfigurationSettingKeys.OnlineThresholdSeconds, DefaultOnlineThresholdSeconds, ct);

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Gets the deduplication TTL as a TimeSpan.
    /// </summary>
    public async Task<TimeSpan> GetDeduplicationTtlAsync(CancellationToken ct = default)
    {
        int seconds = await GetIntSettingAsync(ServerConfigurationSettingKeys.DeduplicationTtlSeconds, DefaultDeduplicationTtlSeconds, ct);

        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// Gets the agent command poll interval in seconds.
    /// </summary>
    public async Task<int> GetAgentCommandPollSecondsAsync(CancellationToken ct = default)
    {
        return await GetIntSettingAsync(ServerConfigurationSettingKeys.AgentCommandPollSeconds, DefaultAgentCommandPollSeconds, ct);
    }

    /// <summary>
    /// Gets the fast telemetry collection interval in seconds.
    /// </summary>
    public async Task<int> GetTelemetryCollectFastSecondsAsync(CancellationToken ct = default)
    {
        return await GetIntSettingAsync(ServerConfigurationSettingKeys.TelemetryCollectFastSeconds, DefaultTelemetryCollectFastSeconds, ct);
    }

    /// <summary>
    /// Gets the slow telemetry collection interval in seconds.
    /// </summary>
    public async Task<int> GetTelemetryCollectSlowSecondsAsync(CancellationToken ct = default)
    {
        return await GetIntSettingAsync(ServerConfigurationSettingKeys.TelemetryCollectSlowSeconds, DefaultTelemetryCollectSlowSeconds, ct);
    }

    /// <summary>
    /// Gets the fast telemetry send interval in seconds.
    /// </summary>
    public async Task<int> GetTelemetrySendFastSecondsAsync(CancellationToken ct = default)
    {
        return await GetIntSettingAsync(ServerConfigurationSettingKeys.TelemetrySendFastSeconds, DefaultTelemetrySendFastSeconds, ct);
    }

    /// <summary>
    /// Gets the slow telemetry send interval in seconds.
    /// </summary>
    public async Task<int> GetTelemetrySendSlowSecondsAsync(CancellationToken ct = default)
    {
        return await GetIntSettingAsync(ServerConfigurationSettingKeys.TelemetrySendSlowSeconds, DefaultTelemetrySendSlowSeconds, ct);
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

        // Fall back to database via the existing cache layer.
        string? value = await _cache.GetSettingAsync(key, ct);
        if (value is not null && int.TryParse(value, out int parsed) && parsed > 0)
        {
            // Store in Redis so other replicas can read it.
            await db.StringSetAsync(redisKey, parsed.ToString(), CacheTtl);

            return parsed;
        }

        return defaultValue;
    }
}
