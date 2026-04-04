// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Background service that consumes MachineState update messages from Redis Streams
/// and applies them as coalesced batch UPDATEs to PostgreSQL.
/// </summary>
public sealed class MachineStateConsumerService : BackgroundService
{
    /// <summary>
    /// Consumer group name shared across all server pods.
    /// </summary>
    internal const string ConsumerGroup = "state-updaters";

    /// <summary>
    /// Maximum number of messages to read per XREADGROUP call.
    /// </summary>
    private const int ReadBatchSize = 100;

    /// <summary>
    /// How long XREADGROUP blocks waiting for new messages.
    /// </summary>
    private const int BlockMilliseconds = 1000;

    /// <summary>
    /// How long a message must be idle before another consumer can claim it.
    /// </summary>
    private static readonly TimeSpan AutoClaimIdleThreshold = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often to run XAUTOCLAIM to reclaim abandoned messages.
    /// </summary>
    private static readonly TimeSpan AutoClaimInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Maximum number of times a message can be delivered before it is dead-lettered.
    /// </summary>
    internal const int MaxDeliveryAttempts = 5;

    /// <summary>
    /// TTL for delivery count keys in Redis. Counts expire if the message is not re-delivered within this window.
    /// </summary>
    private static readonly TimeSpan DeliveryCountTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Prefix for delivery count keys used to track poison messages.
    /// </summary>
    private const string DeliveryCountPrefix = "dead-letter:";

    private readonly IConnectionMultiplexer _redis;
    private readonly IMachineStateUpdater _stateUpdater;
    private readonly ILogger<MachineStateConsumerService> _logger;
    private readonly string _consumerName;
    private DateTimeOffset _lastAutoClaimTime = DateTimeOffset.MinValue;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineStateConsumerService"/> class.
    /// </summary>
    public MachineStateConsumerService(
        IConnectionMultiplexer redis,
        IMachineStateUpdater stateUpdater,
        ILogger<MachineStateConsumerService> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _stateUpdater = stateUpdater ?? throw new ArgumentNullException(nameof(stateUpdater));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Use hostname plus a unique suffix to avoid consumer name collisions
        // during rolling updates where pod names may be recycled.
        _consumerName = $"{Environment.MachineName}-{Guid.NewGuid():N}";
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Brief delay to let the application finish starting.
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        await EnsureConsumerGroupsExistAsync();

        _logger.LogInformation("MachineState consumer started with name {ConsumerName}", _consumerName);

        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await ProcessAllPartitionsAsync(stoppingToken);
                await TryAutoClaimAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in MachineState consumer loop");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("MachineState consumer stopped");
    }

    private async Task EnsureConsumerGroupsExistAsync()
    {
        IDatabase db = _redis.GetDatabase();

        for (int i = 0; i < MachineStateQueueService.PartitionCount; i++)
        {
            string streamKey = $"{MachineStateQueueService.StreamPrefix}{i}";
            try
            {
                await db.StreamCreateConsumerGroupAsync(streamKey, ConsumerGroup, StreamPosition.NewMessages, createStream: true);
            }
            catch (RedisServerException ex) when (ex.Message.Contains("BUSYGROUP"))
            {
                // Consumer group already exists, which is expected on restart.
            }
        }
    }

    private async Task ProcessAllPartitionsAsync(CancellationToken ct)
    {
        IDatabase db = _redis.GetDatabase();

        // Build XREADGROUP args for all partitions at once.
        List<RedisKey> streamKeys = [];
        for (int i = 0; i < MachineStateQueueService.PartitionCount; i++)
        {
            streamKeys.Add($"{MachineStateQueueService.StreamPrefix}{i}");
        }

        // Read from all partition streams using XREADGROUP with the ">" special ID
        // to get only new, undelivered messages.
        RedisStream[] results = await db.StreamReadGroupAsync(
            [.. streamKeys.Select(k => new StreamPosition(k, StreamPosition.NewMessages))],
            ConsumerGroup,
            _consumerName,
            ReadBatchSize,
            noAck: false);

        if (results.Length == 0)
        {
            // No messages available; XREADGROUP returned immediately or after block timeout.
            // Brief pause to avoid a tight loop when there is no data.
            await Task.Delay(BlockMilliseconds, ct);

            return;
        }

        // Coalesce all messages by machine ID, grouping each machine's updates by telemetry type.
        Dictionary<long, List<StateUpdateMessage>> coalesced = [];

        List<(RedisKey StreamKey, RedisValue MessageId)> toAck = [];
        List<(RedisKey StreamKey, RedisValue MessageId)> toDeadLetter = [];

        foreach (RedisStream stream in results)
        {
            foreach (StreamEntry entry in stream.Entries)
            {
                // Check delivery count to detect poison messages that consistently fail.
                long deliveryCount = await IncrementDeliveryCountAsync(db, stream.Key.ToString(), entry.Id);
                if (deliveryCount > MaxDeliveryAttempts)
                {
                    _logger.LogError(
                        "Dead-lettering message {EntryId} in {StreamKey} after {Count} delivery attempts",
                        entry.Id, stream.Key, deliveryCount);
                    toDeadLetter.Add((stream.Key, entry.Id));

                    continue;
                }

                toAck.Add((stream.Key, entry.Id));

                string? machineIdStr = entry["machine_id"];
                string? itemsJson = entry["items"];

                if (string.IsNullOrEmpty(machineIdStr) || string.IsNullOrEmpty(itemsJson))
                {
                    _logger.LogWarning("Skipping malformed stream entry {EntryId} in {StreamKey}", entry.Id, stream.Key);

                    continue;
                }

                if (long.TryParse(machineIdStr, out long machineId) == false)
                {
                    _logger.LogWarning("Invalid machine ID {MachineId} in stream entry {EntryId}", machineIdStr, entry.Id);

                    continue;
                }

                try
                {
                    List<StateUpdateMessage>? items = JsonSerializer.Deserialize<List<StateUpdateMessage>>(itemsJson, JsonDefaults.SnakeCase);
                    if (items is null || items.Count == 0)
                    {
                        continue;
                    }

                    if (coalesced.TryGetValue(machineId, out List<StateUpdateMessage>? existing) == false)
                    {
                        existing = [];
                        coalesced[machineId] = existing;
                    }

                    existing.AddRange(items);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize state update items for machine {MachineId}", machineId);
                }
            }
        }

        // ACK dead-lettered messages immediately so they are not re-delivered.
        foreach (IGrouping<RedisKey, (RedisKey StreamKey, RedisValue MessageId)> group in toDeadLetter.GroupBy(a => a.StreamKey))
        {
            RedisValue[] messageIds = group.Select(g => g.MessageId).ToArray();
            await db.StreamAcknowledgeAsync(group.Key, ConsumerGroup, messageIds);
        }

        // Process all coalesced updates: group by type across all machines, then batch update.
        if (coalesced.Count > 0)
        {
            try
            {
                await _stateUpdater.UpdateBatchAsync(coalesced, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply batch state updates for {Count} machines", coalesced.Count);

                // Don't ACK on failure — messages will be retried via XAUTOCLAIM.
                // Delivery counts are preserved in Redis so poison messages will
                // eventually be dead-lettered after MaxDeliveryAttempts.

                return;
            }
        }

        // ACK all successfully processed messages and clean up their delivery count keys.
        foreach (IGrouping<RedisKey, (RedisKey StreamKey, RedisValue MessageId)> group in toAck.GroupBy(a => a.StreamKey))
        {
            RedisValue[] messageIds = group.Select(g => g.MessageId).ToArray();
            await db.StreamAcknowledgeAsync(group.Key, ConsumerGroup, messageIds);
        }

        await ClearDeliveryCountsAsync(db, toAck);
    }

    /// <summary>
    /// Increments the delivery count for a message and returns the new count.
    /// Uses Redis INCR with a TTL so that counts auto-expire if the message is not re-delivered.
    /// </summary>
    private static async Task<long> IncrementDeliveryCountAsync(IDatabase db, string streamKey, RedisValue messageId)
    {
        string key = $"{DeliveryCountPrefix}{streamKey}:{messageId}";
        long count = await db.StringIncrementAsync(key);
        await db.KeyExpireAsync(key, DeliveryCountTtl);

        return count;
    }

    /// <summary>
    /// Clears delivery count keys for successfully processed messages.
    /// </summary>
    private static async Task ClearDeliveryCountsAsync(IDatabase db, List<(RedisKey StreamKey, RedisValue MessageId)> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        RedisKey[] keys = entries
            .Select(e => (RedisKey)$"{DeliveryCountPrefix}{e.StreamKey}:{e.MessageId}")
            .ToArray();
        await db.KeyDeleteAsync(keys);
    }

    private async Task TryAutoClaimAsync(CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastAutoClaimTime < AutoClaimInterval)
        {
            return;
        }

        _lastAutoClaimTime = now;
        IDatabase db = _redis.GetDatabase();

        for (int i = 0; i < MachineStateQueueService.PartitionCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            string streamKey = $"{MachineStateQueueService.StreamPrefix}{i}";
            try
            {
                StreamAutoClaimResult result = await db.StreamAutoClaimAsync(
                    streamKey,
                    ConsumerGroup,
                    _consumerName,
                    (long)AutoClaimIdleThreshold.TotalMilliseconds,
                    "0-0",
                    count: 50);

                if (result.ClaimedEntries.Length > 0)
                {
                    _logger.LogInformation("Auto-claimed {Count} messages from partition {Partition}",
                        result.ClaimedEntries.Length, i);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-claim messages from partition {Partition}", i);
            }
        }
    }
}
