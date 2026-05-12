// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.ServerConfiguration;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.Telemetry;

/// <summary>
/// Redis-backed implementation of <see cref="ITelemetryDeduplicationService"/> using SET NX with TTL.
/// </summary>
public sealed class RedisTelemetryDeduplicationService : ITelemetryDeduplicationService
{
    private const string KeyPrefix = "telemetry:dedup:";

    private readonly IConnectionMultiplexer _redis;
    private readonly ServerConfigurationService _configService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisTelemetryDeduplicationService"/> class.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="configService">The server configuration service for runtime settings.</param>
    public RedisTelemetryDeduplicationService(IConnectionMultiplexer redis, ServerConfigurationService configService)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
    }

    /// <inheritdoc/>
    public async Task<bool> TryMarkSeenAsync(string eventId)
    {
        TimeSpan ttl = await _configService.GetDeduplicationTtlAsync();
        IDatabase db = _redis.GetDatabase();
        string key = KeyPrefix + eventId;

        bool wasSet = await db.StringSetAsync(key, "1", ttl, When.NotExists);

        return wasSet;
    }

    /// <inheritdoc/>
    public async Task<Dictionary<string, bool>> TryMarkSeenBatchAsync(IEnumerable<string> eventIds)
    {
        TimeSpan ttl = await _configService.GetDeduplicationTtlAsync();
        IDatabase db = _redis.GetDatabase();
        IBatch batch = db.CreateBatch();

        List<(string EventId, Task<bool> Task)> pending = [];
        foreach (string eventId in eventIds)
        {
            string key = KeyPrefix + eventId;
            Task<bool> task = batch.StringSetAsync(key, "1", ttl, When.NotExists);
            pending.Add((eventId, task));
        }

        batch.Execute();
        await Task.WhenAll(pending.Select(p => p.Task));

        Dictionary<string, bool> result = new(pending.Count);
        foreach ((string eventId, Task<bool> task) in pending)
        {
            result[eventId] = task.Result;
        }

        return result;
    }
}
