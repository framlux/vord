// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Polly;
using Polly.Registry;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Redis-backed implementation of <see cref="IMachinePingService"/>. Each machine's reported
/// capabilities are stored as a single key, with a TTL so a machine that stops reporting
/// (decommissioned or removed) self-evicts instead of leaking a key forever. Every report refreshes
/// the key and its TTL. Liveness is not stored here: it is decided by the health sweep from the
/// timestamps on MachineStateSummary.
/// </summary>
public sealed class RedisMachinePingService : IMachinePingService
{
    // TTL for a machine's Redis capabilities key. Comfortably longer than any configuration fetch
    // interval; its purpose is to evict keys for machines that never report again instead of
    // leaking them forever.
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

    private static string GetCapabilitiesKey(long machineId)
    {
        return $"machine:caps:{machineId}";
    }
}
