// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Polly;
using Polly.Registry;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Redis-backed implementation of <see cref="IMachinePingService"/>. Each machine's last ping is
/// stored as a single key holding the timestamp, with a TTL so a machine that stops reporting
/// (decommissioned or removed) self-evicts instead of leaking a key forever. Every ping refreshes
/// the key and its TTL. LastSeenAt on MachineStateSummary is updated by the streaming worker, not here.
/// </summary>
public sealed class RedisMachinePingService : IMachinePingService
{
    // TTL for a machine's Redis keys (last-ping and capabilities). Comfortably longer than any
    // online threshold, so online/offline decisions are unaffected; its purpose is to evict keys
    // for machines that never report again instead of leaking them forever.
    private static readonly TimeSpan KeyRetention = TimeSpan.FromDays(7);

    private readonly IConnectionMultiplexer _redis;
    private readonly ResiliencePipeline _retryPipeline;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisMachinePingService"/> class.
    /// </summary>
    public RedisMachinePingService(
        IConnectionMultiplexer redis,
        ResiliencePipelineProvider<string> pipelineProvider)
    {
        ArgumentNullException.ThrowIfNull(pipelineProvider);

        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _retryPipeline = pipelineProvider.GetPipeline("redis-ping");
    }

    /// <inheritdoc/>
    public async Task RecordPingAsync(long machineId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await ExecuteWithRetryAsync("RecordPing", async () =>
        {
            IDatabase db = _redis.GetDatabase();
            string key = GetKey(machineId);
            long nowMs = now.ToUnixTimeMilliseconds();

            // Overwrite the single last-ping value and refresh its TTL on every ping.
            await db.StringSetAsync(key, nowMs.ToString(), KeyRetention);
        });
    }

    /// <inheritdoc/>
    public async Task<DateTimeOffset?> GetLastPingAsync(long machineId)
    {
        IDatabase db = _redis.GetDatabase();
        RedisValue value = await db.StringGetAsync(GetKey(machineId));

        return ParseLastPing(value);
    }

    /// <inheritdoc/>
    public async Task<bool> IsOnlineAsync(long machineId, TimeSpan threshold)
    {
        DateTimeOffset? lastPing = await GetLastPingAsync(machineId);
        if (lastPing is null)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - lastPing.Value <= threshold;
    }

    /// <inheritdoc/>
    public async Task<Dictionary<long, bool>> AreOnlineAsync(IEnumerable<long> machineIds, TimeSpan threshold)
    {
        Dictionary<long, DateTimeOffset?> lastPings = await GetLastPingsAsync(machineIds);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Dictionary<long, bool> result = new(lastPings.Count);
        foreach (KeyValuePair<long, DateTimeOffset?> kvp in lastPings)
        {
            result[kvp.Key] = kvp.Value.HasValue && now - kvp.Value.Value <= threshold;
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<Dictionary<long, DateTimeOffset?>> GetLastPingsAsync(IEnumerable<long> machineIds)
    {
        IDatabase db = _redis.GetDatabase();
        IBatch batch = db.CreateBatch();

        List<(long Id, Task<RedisValue> Task)> pending = [];
        foreach (long machineId in machineIds)
        {
            string key = GetKey(machineId);
            Task<RedisValue> task = batch.StringGetAsync(key);
            pending.Add((machineId, task));
        }

        batch.Execute();
        await Task.WhenAll(pending.Select(p => p.Task));

        Dictionary<long, DateTimeOffset?> result = new(pending.Count);
        foreach ((long id, Task<RedisValue> task) in pending)
        {
            result[id] = ParseLastPing(task.Result);
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task SetAgentCapabilitiesAsync(long machineId, ulong capabilities)
    {
        await ExecuteWithRetryAsync("SetAgentCapabilities", async () =>
        {
            IDatabase db = _redis.GetDatabase();
            string key = GetCapabilitiesKey(machineId);
            await db.StringSetAsync(key, capabilities.ToString(), KeyRetention);
        });
    }

    /// <inheritdoc/>
    public async Task<ulong> GetAgentCapabilitiesAsync(long machineId)
    {
        IDatabase db = _redis.GetDatabase();
        RedisValue value = await db.StringGetAsync(GetCapabilitiesKey(machineId));
        if (value.IsNullOrEmpty)
        {
            return 0;
        }

        return ulong.TryParse((string?)value, out ulong result) ? result : 0;
    }

    /// <inheritdoc/>
    public async Task<Dictionary<long, ulong>> GetAgentCapabilitiesBatchAsync(IEnumerable<long> machineIds)
    {
        IDatabase db = _redis.GetDatabase();
        IBatch batch = db.CreateBatch();

        List<(long Id, Task<RedisValue> Task)> pending = [];
        foreach (long machineId in machineIds)
        {
            string key = GetCapabilitiesKey(machineId);
            Task<RedisValue> task = batch.StringGetAsync(key);
            pending.Add((machineId, task));
        }

        batch.Execute();
        await Task.WhenAll(pending.Select(p => p.Task));

        Dictionary<long, ulong> result = new(pending.Count);
        foreach ((long id, Task<RedisValue> task) in pending)
        {
            RedisValue value = task.Result;
            ulong capabilities = 0;
            if ((value.IsNullOrEmpty == false) && ulong.TryParse((string?)value, out ulong parsed))
            {
                capabilities = parsed;
            }

            result[id] = capabilities;
        }

        return result;
    }

    /// <summary>
    /// Runs <paramref name="action"/> through the shared "redis-ping" retry pipeline,
    /// threading <paramref name="operationName"/> through the <see cref="ResilienceContext"/>
    /// so retry warnings are attributed to the calling operation, matching the deleted
    /// RetryHelper's operationName parameter.
    /// </summary>
    private async Task ExecuteWithRetryAsync(string operationName, Func<ValueTask> action)
    {
        ResilienceContext context = ResilienceContextPool.Shared.Get(operationName, CancellationToken.None);
        try
        {
            await _retryPipeline.ExecuteAsync(_ => action(), context);
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    private static DateTimeOffset? ParseLastPing(RedisValue value)
    {
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        return long.TryParse((string?)value, out long ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
    }

    private static string GetKey(long machineId)
    {
        return $"machine:ping:{machineId}";
    }

    private static string GetCapabilitiesKey(long machineId)
    {
        return $"machine:caps:{machineId}";
    }
}
