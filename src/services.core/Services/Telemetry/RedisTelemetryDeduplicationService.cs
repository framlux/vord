// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.ServerConfiguration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Diagnostics.Metrics;

namespace Framlux.FleetManagement.Services.Core.Telemetry;

/// <summary>
/// Redis-backed implementation of <see cref="ITelemetryDeduplicationService"/> using SET NX with TTL.
/// </summary>
public sealed class RedisTelemetryDeduplicationService : ITelemetryDeduplicationService
{
    private const string KeyPrefix = "telemetry:dedup:";

    /// <summary>Meter name for telemetry-dedup instruments.</summary>
    public const string MeterName = "Framlux.FleetManagement.Services.Core.TelemetryDedup";

    private static readonly Meter DedupMeter = new(MeterName);

    /// <summary>
    /// Counts batches processed as all-unseen because Redis dedup was unavailable (fail-open). The
    /// Postgres unique index remains the backstop, so duplicates are still dropped at insert time.
    /// </summary>
    private static readonly Counter<long> FailOpenCounter = DedupMeter.CreateCounter<long>(
        "telemetry.dedup_failopen",
        unit: "batches",
        description: "Telemetry batches admitted without Redis dedup because Redis was unavailable.");

    private readonly IConnectionMultiplexer _redis;
    private readonly ServerConfigurationService _configService;
    private readonly ILogger<RedisTelemetryDeduplicationService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisTelemetryDeduplicationService"/> class.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="configService">The server configuration service for runtime settings.</param>
    /// <param name="logger">Logger used to warn when dedup fails open on a Redis outage.</param>
    public RedisTelemetryDeduplicationService(IConnectionMultiplexer redis, ServerConfigurationService configService, ILogger<RedisTelemetryDeduplicationService> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        List<string> ids = eventIds as List<string> ?? eventIds.ToList();

        try
        {
            TimeSpan ttl = await _configService.GetDeduplicationTtlAsync();
            IDatabase db = _redis.GetDatabase();
            IBatch batch = db.CreateBatch();

            List<(string EventId, Task<bool> Task)> pending = [];
            foreach (string eventId in ids)
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
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            // Fail open: report every id as newly-seen so processing proceeds. The Postgres unique index
            // on (SourceEventId, ReceivedAt) is the layer-2 dedup backstop that drops any true duplicate
            // at insert time, so correctness is preserved while Redis is unavailable.
            FailOpenCounter.Add(1);
            _logger.LogWarning(ex, "Redis unavailable for telemetry dedup; failing open for {Count} events (DB unique index will dedup)", ids.Count);

            return ids.ToDictionary(id => id, _ => true);
        }
    }
}
