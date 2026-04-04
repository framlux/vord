// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Publishes MachineState update messages to partitioned Redis Streams.
/// Stream key: <c>machine-state:{machineId % partitionCount}</c>.
/// </summary>
public sealed class MachineStateQueueService : IMachineStateQueueService
{
    /// <summary>
    /// Number of stream partitions. All updates for the same machine go to the same partition.
    /// </summary>
    internal const int PartitionCount = 8;

    /// <summary>
    /// Prefix for Redis Stream keys.
    /// </summary>
    internal const string StreamPrefix = "machine-state:";

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<MachineStateQueueService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineStateQueueService"/> class.
    /// </summary>
    public MachineStateQueueService(IConnectionMultiplexer redis, ILogger<MachineStateQueueService> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public async Task PublishAsync(long machineId, IReadOnlyList<StateUpdateMessage> items, CancellationToken ct)
    {
        if (items.Count == 0)
        {

            return;
        }

        await RetryHelper.ExecuteWithRetryAsync(async () =>
        {
            IDatabase db = _redis.GetDatabase();
            string streamKey = GetStreamKey(machineId);

            // Serialize all items into a single stream entry for this machine.
            string itemsJson = JsonSerializer.Serialize(items, JsonDefaults.SnakeCase);

            NameValueEntry[] fields =
            [
                new NameValueEntry("machine_id", machineId.ToString()),
                new NameValueEntry("items", itemsJson),
            ];

            await db.StreamAddAsync(streamKey, fields, maxLength: 50000, useApproximateMaxLength: true);
        }, logger: _logger, operationName: "PublishStateUpdate", ct: ct);
    }

    /// <summary>
    /// Gets the Redis Stream key for a given machine ID, using consistent hash partitioning.
    /// </summary>
    internal static string GetStreamKey(long machineId)
    {
        // Use absolute value to handle negative IDs safely.
        long partition = Math.Abs(machineId % PartitionCount);

        return $"{StreamPrefix}{partition}";
    }
}
