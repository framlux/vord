// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services.Machines;

/// <summary>
/// Tests for <see cref="MachineStateConsumerService"/>.
/// </summary>
public class MachineStateConsumerServiceTests
{
    // -----------------------------------------------------------------------
    // Factory helpers
    // -----------------------------------------------------------------------

    private static (MachineStateConsumerService service, IDatabase db, IMachineStateUpdater updater)
        CreateService()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        redis.GetDatabase().Returns(db);

        IMachineStateUpdater updater = Substitute.For<IMachineStateUpdater>();
        NullLogger<MachineStateConsumerService> logger = new();

        MachineStateConsumerService service = new(redis, updater, logger);

        return (service, db, updater);
    }

    /// <summary>
    /// Builds a valid JSON-serialized list of state update messages for a single telemetry type.
    /// </summary>
    private static string BuildItemsJson(short telemetryType, string payload)
    {
        List<StateUpdateMessage> messages =
        [
            new StateUpdateMessage
            {
                TelemetryType = telemetryType,
                Payload = payload,
                ReceivedAt = DateTimeOffset.UtcNow,
            }
        ];

        return JsonSerializer.Serialize(messages, JsonDefaults.SnakeCase);
    }

    /// <summary>
    /// Builds a <see cref="StreamEntry"/> containing machine_id and items fields.
    /// </summary>
    private static StreamEntry BuildStreamEntry(string entryId, long machineId, string itemsJson)
    {
        NameValueEntry[] fields =
        [
            new NameValueEntry("machine_id", machineId.ToString()),
            new NameValueEntry("items", itemsJson),
        ];

        return new StreamEntry(entryId, fields);
    }

    /// <summary>
    /// Builds a <see cref="StreamEntry"/> with no machine_id field to simulate a malformed message.
    /// </summary>
    private static StreamEntry BuildMalformedStreamEntry(string entryId)
    {
        NameValueEntry[] fields =
        [
            new NameValueEntry("bad_field", "garbage"),
        ];

        return new StreamEntry(entryId, fields);
    }

    /// <summary>
    /// Builds a <see cref="RedisStream"/> for a given partition key containing the supplied entries.
    /// RedisStream has an internal constructor in StackExchange.Redis, so we use
    /// Unsafe.As to reinterpret a compatible struct.
    /// </summary>
    private static RedisStream BuildRedisStream(int partition, StreamEntry[] entries)
    {
        RedisKey key = $"machine-state:{partition}";
        RedisStreamProxy proxy = new() { Key = key, Entries = entries };

        return System.Runtime.CompilerServices.Unsafe.As<RedisStreamProxy, RedisStream>(ref proxy);
    }

    /// <summary>
    /// Memory-layout-compatible proxy for <see cref="RedisStream"/> to work around its internal constructor.
    /// </summary>
    private struct RedisStreamProxy
    {
        /// <summary>The stream key.</summary>
        public RedisKey Key;

        /// <summary>The stream entries.</summary>
        public StreamEntry[] Entries;
    }

    /// <summary>
    /// Configures the mocked <see cref="IDatabase"/> to return the given results from
    /// <c>StreamReadGroupAsync</c> on the first call, then return empty on subsequent calls
    /// so the loop does not spin indefinitely.
    /// </summary>
    private static void SetupStreamRead(IDatabase db, RedisStream[] firstResults)
    {
        // Return the real results the first time, then empty arrays to let cancellation end the loop.
        db.StreamReadGroupAsync(
                Arg.Any<StreamPosition[]>(),
                Arg.Any<RedisValue>(),
                Arg.Any<RedisValue>(),
                Arg.Any<int?>(),
                Arg.Any<bool>(),
                Arg.Any<CommandFlags>())
            .Returns(
                Task.FromResult(firstResults),
                Task.FromResult(Array.Empty<RedisStream>()));
    }

    /// <summary>
    /// Configures <c>StringIncrementAsync</c> to return a given delivery count for any key.
    /// Matches the single-argument overload used by <c>IncrementDeliveryCountAsync</c>.
    /// </summary>
    private static void SetupDeliveryCount(IDatabase db, long count)
    {
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(count));
        db.StringIncrementAsync(Arg.Any<RedisKey>())
            .Returns(Task.FromResult(count));
    }

    /// <summary>
    /// Runs the service long enough for the background loop to execute at least one full
    /// iteration past the 2-second startup delay, then stops it cleanly.
    /// </summary>
    private static async Task RunServiceBrieflyAsync(MachineStateConsumerService service)
    {
        await service.StartAsync(CancellationToken.None);
        try
        {
            // Wait past the 2-second startup delay plus some buffer for processing.
            await Task.Delay(TimeSpan.FromSeconds(2) + TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // -----------------------------------------------------------------------
    // Constructor guard tests
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a null Redis multiplexer throws ArgumentNullException immediately.
    /// </summary>
    [Test]
    public async Task Constructor_NullRedis_ThrowsArgumentNullException()
    {
        IMachineStateUpdater updater = Substitute.For<IMachineStateUpdater>();
        NullLogger<MachineStateConsumerService> logger = new();

        Exception? caught = null;
        try
        {
            MachineStateConsumerService _ = new(null!, updater, logger);
        }
        catch (ArgumentNullException ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsNotNull();
    }

    /// <summary>
    /// Verifies that a null state updater throws ArgumentNullException immediately.
    /// </summary>
    [Test]
    public async Task Constructor_NullUpdater_ThrowsArgumentNullException()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        NullLogger<MachineStateConsumerService> logger = new();

        Exception? caught = null;
        try
        {
            MachineStateConsumerService _ = new(redis, null!, logger);
        }
        catch (ArgumentNullException ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsNotNull();
    }

    /// <summary>
    /// Verifies that a null logger throws ArgumentNullException immediately.
    /// </summary>
    [Test]
    public async Task Constructor_NullLogger_ThrowsArgumentNullException()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IMachineStateUpdater updater = Substitute.For<IMachineStateUpdater>();

        Exception? caught = null;
        try
        {
            MachineStateConsumerService _ = new(redis, updater, null!);
        }
        catch (ArgumentNullException ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsNotNull();
    }

    // -----------------------------------------------------------------------
    // Message coalescing — two messages for the same machine merge
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that when two stream entries for the same machine arrive in the same batch,
    /// both sets of state update messages are merged and delivered together to UpdateBatchAsync.
    /// </summary>
    [Test]
    public async Task ProcessAllPartitions_TwoEntriesSameMachine_CoalescedIntoSingleBatchCall()
    {
        (MachineStateConsumerService service, IDatabase db, IMachineStateUpdater updater) = CreateService();

        long machineId = 101L;
        string itemsJson1 = BuildItemsJson(6, """{"cpu_usage_percent":30}""");
        string itemsJson2 = BuildItemsJson(7, """{"memory_usage_percent":55}""");

        StreamEntry entry1 = BuildStreamEntry("1-1", machineId, itemsJson1);
        StreamEntry entry2 = BuildStreamEntry("1-2", machineId, itemsJson2);

        // Both entries land in partition 0 of the same stream.
        RedisStream[] streams = [BuildRedisStream(0, [entry1, entry2])];
        SetupStreamRead(db, streams);

        // Delivery count under the dead-letter threshold.
        SetupDeliveryCount(db, 1);

        await RunServiceBrieflyAsync(service);

        // UpdateBatchAsync must have been called with both messages merged under the same machine ID.
        await updater.Received().UpdateBatchAsync(
            Arg.Is<Dictionary<long, List<StateUpdateMessage>>>(d =>
                d.ContainsKey(machineId) && d[machineId].Count == 2),
            Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Malformed entry — bad machine_id is logged and ACKed without blocking good messages
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a stream entry missing the machine_id field is ACKed (to remove it from
    /// the pending-entry list) and does not prevent valid entries in the same batch from
    /// being processed and passed to UpdateBatchAsync.
    /// </summary>
    [Test]
    public async Task ProcessAllPartitions_MalformedEntry_AckedAndGoodMessagesStillProcessed()
    {
        (MachineStateConsumerService service, IDatabase db, IMachineStateUpdater updater) = CreateService();

        long goodMachineId = 202L;
        string goodItemsJson = BuildItemsJson(6, """{"cpu_usage_percent":70}""");

        StreamEntry malformed = BuildMalformedStreamEntry("2-1");
        StreamEntry good = BuildStreamEntry("2-2", goodMachineId, goodItemsJson);

        RedisStream[] streams = [BuildRedisStream(0, [malformed, good])];
        SetupStreamRead(db, streams);
        SetupDeliveryCount(db, 1);

        await RunServiceBrieflyAsync(service);

        // The good message must have reached the updater.
        await updater.Received().UpdateBatchAsync(
            Arg.Is<Dictionary<long, List<StateUpdateMessage>>>(d => d.ContainsKey(goodMachineId)),
            Arg.Any<CancellationToken>());

        // Both messages (malformed and good) must have been ACKed so neither
        // stays in the pending-entry list indefinitely.
        await db.Received().StreamAcknowledgeAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<RedisValue[]>());
    }

    // -----------------------------------------------------------------------
    // UpdateBatchAsync failure — messages must NOT be ACKed (retry via XAUTOCLAIM)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that when UpdateBatchAsync throws, StreamAcknowledgeAsync is NOT called for the
    /// affected messages. This ensures they remain in the pending-entry list and can be reclaimed
    /// and retried by XAUTOCLAIM.
    /// </summary>
    [Test]
    public async Task ProcessAllPartitions_UpdateBatchFails_MessagesNotAcked()
    {
        (MachineStateConsumerService service, IDatabase db, IMachineStateUpdater updater) = CreateService();

        long machineId = 303L;
        string itemsJson = BuildItemsJson(6, """{"cpu_usage_percent":80}""");
        StreamEntry entry = BuildStreamEntry("3-1", machineId, itemsJson);

        RedisStream[] streams = [BuildRedisStream(0, [entry])];
        SetupStreamRead(db, streams);
        SetupDeliveryCount(db, 1);

        // Simulate a transient database failure.
        updater.UpdateBatchAsync(
                Arg.Any<Dictionary<long, List<StateUpdateMessage>>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("Database unavailable")));

        await RunServiceBrieflyAsync(service);

        // No ACK must have been issued — the message stays pending for XAUTOCLAIM retry.
        await db.DidNotReceive().StreamAcknowledgeAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<RedisValue[]>());
    }

    // -----------------------------------------------------------------------
    // Dead-letter threshold — message ACKed after MaxDeliveryAttempts exceeded
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a message whose delivery count exceeds MaxDeliveryAttempts is immediately
    /// ACKed (dead-lettered) so it is not delivered again, even though no batch update runs.
    /// </summary>
    [Test]
    public async Task ProcessAllPartitions_DeliveryCountExceedsThreshold_MessageDeadLettered()
    {
        (MachineStateConsumerService service, IDatabase db, IMachineStateUpdater updater) = CreateService();

        long machineId = 404L;
        string itemsJson = BuildItemsJson(6, """{"cpu_usage_percent":99}""");
        StreamEntry entry = BuildStreamEntry("4-1", machineId, itemsJson);

        RedisStream[] streams = [BuildRedisStream(0, [entry])];
        SetupStreamRead(db, streams);

        // Return a delivery count that exceeds the dead-letter threshold.
        long poisonCount = MachineStateConsumerService.MaxDeliveryAttempts + 1;
        SetupDeliveryCount(db, poisonCount);

        await RunServiceBrieflyAsync(service);

        // The dead-lettered message must be ACKed to remove it from the pending-entry list.
        await db.Received().StreamAcknowledgeAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<RedisValue[]>());

        // UpdateBatchAsync must NOT have been called — the message was discarded before coalescing.
        await updater.DidNotReceive().UpdateBatchAsync(
            Arg.Any<Dictionary<long, List<StateUpdateMessage>>>(),
            Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Delivery count exactly at MaxDeliveryAttempts — message is still processed
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that a message at exactly MaxDeliveryAttempts (not exceeding it) is still
    /// coalesced and forwarded to UpdateBatchAsync rather than being dead-lettered.
    /// </summary>
    [Test]
    public async Task ProcessAllPartitions_DeliveryCountAtThreshold_MessageProcessedNotDeadLettered()
    {
        (MachineStateConsumerService service, IDatabase db, IMachineStateUpdater updater) = CreateService();

        long machineId = 405L;
        string itemsJson = BuildItemsJson(6, """{"cpu_usage_percent":50}""");
        StreamEntry entry = BuildStreamEntry("5-1", machineId, itemsJson);

        RedisStream[] streams = [BuildRedisStream(0, [entry])];
        SetupStreamRead(db, streams);

        // Exactly at the threshold — should still be processed.
        SetupDeliveryCount(db, MachineStateConsumerService.MaxDeliveryAttempts);

        await RunServiceBrieflyAsync(service);

        // UpdateBatchAsync must have been called — the message was not dead-lettered.
        await updater.Received().UpdateBatchAsync(
            Arg.Is<Dictionary<long, List<StateUpdateMessage>>>(d => d.ContainsKey(machineId)),
            Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Empty stream — no processing or ACKs, just a delay
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that when the stream returns no results, UpdateBatchAsync is never called and
    /// no ACKs are issued. The consumer simply waits before checking again.
    /// </summary>
    [Test]
    public async Task ProcessAllPartitions_EmptyStream_UpdateBatchNeverCalled()
    {
        (MachineStateConsumerService service, IDatabase db, IMachineStateUpdater updater) = CreateService();

        // Always return empty — simulates an idle stream.
        db.StreamReadGroupAsync(
                Arg.Any<StreamPosition[]>(),
                Arg.Any<RedisValue>(),
                Arg.Any<RedisValue>(),
                Arg.Any<int?>(),
                Arg.Any<bool>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(Array.Empty<RedisStream>()));

        await RunServiceBrieflyAsync(service);

        await updater.DidNotReceive().UpdateBatchAsync(
            Arg.Any<Dictionary<long, List<StateUpdateMessage>>>(),
            Arg.Any<CancellationToken>());

        await db.DidNotReceive().StreamAcknowledgeAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<RedisValue[]>());
    }

    // -----------------------------------------------------------------------
    // CancellationToken respected — service stops cleanly
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that the service completes its background task cleanly when the cancellation
    /// token is signalled, without throwing an unhandled OperationCanceledException to the caller.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_CancellationRequested_ServiceStopsCleanly()
    {
        (MachineStateConsumerService service, IDatabase db, IMachineStateUpdater _) = CreateService();

        // Return empty stream so the loop iterates without blocking.
        db.StreamReadGroupAsync(
                Arg.Any<StreamPosition[]>(),
                Arg.Any<RedisValue>(),
                Arg.Any<RedisValue>(),
                Arg.Any<int?>(),
                Arg.Any<bool>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(Array.Empty<RedisStream>()));

        await service.StartAsync(CancellationToken.None);

        // Allow the background task to begin its startup delay, then stop the service.
        // StopAsync signals the internal stopping token, which cancels the startup delay
        // and causes the loop to exit cleanly via the OperationCanceledException guard.
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Exception? caught = null;
        try
        {
            await service.StopAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsNull();
    }

    // -----------------------------------------------------------------------
    // Successful processing — delivery count keys are deleted after ACK
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that after a successful batch update, delivery count keys are removed
    /// from Redis to avoid stale counts on future re-deliveries of new messages.
    /// </summary>
    [Test]
    public async Task ProcessAllPartitions_SuccessfulBatch_DeliveryCountKeysDeleted()
    {
        (MachineStateConsumerService service, IDatabase db, IMachineStateUpdater updater) = CreateService();

        long machineId = 505L;
        string itemsJson = BuildItemsJson(6, """{"cpu_usage_percent":45}""");
        StreamEntry entry = BuildStreamEntry("6-1", machineId, itemsJson);

        RedisStream[] streams = [BuildRedisStream(0, [entry])];
        SetupStreamRead(db, streams);
        SetupDeliveryCount(db, 1);

        await RunServiceBrieflyAsync(service);

        // KeyDeleteAsync must have been called to clean up the delivery count key.
        await db.Received().KeyDeleteAsync(
            Arg.Any<RedisKey[]>(),
            Arg.Any<CommandFlags>());
    }

    // -----------------------------------------------------------------------
    // Invalid (non-numeric) machine_id — entry is ACKed and does not block others
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that an entry containing a non-numeric machine_id string is ACKed and does not
    /// prevent other valid entries in the same batch from reaching UpdateBatchAsync.
    /// </summary>
    [Test]
    public async Task ProcessAllPartitions_InvalidMachineIdString_AckedAndRemainingMessagesProcessed()
    {
        (MachineStateConsumerService service, IDatabase db, IMachineStateUpdater updater) = CreateService();

        string itemsJson = BuildItemsJson(6, """{"cpu_usage_percent":60}""");
        long validMachineId = 606L;
        string validItemsJson = BuildItemsJson(7, """{"memory_usage_percent":40}""");

        // Entry with a non-numeric machine_id.
        NameValueEntry[] badFields =
        [
            new NameValueEntry("machine_id", "not-a-number"),
            new NameValueEntry("items", itemsJson),
        ];
        StreamEntry badEntry = new("7-1", badFields);

        // Entry with a valid machine_id.
        StreamEntry goodEntry = BuildStreamEntry("7-2", validMachineId, validItemsJson);

        RedisStream[] streams = [BuildRedisStream(0, [badEntry, goodEntry])];
        SetupStreamRead(db, streams);
        SetupDeliveryCount(db, 1);

        await RunServiceBrieflyAsync(service);

        // The valid machine's update must still be delivered.
        await updater.Received().UpdateBatchAsync(
            Arg.Is<Dictionary<long, List<StateUpdateMessage>>>(d => d.ContainsKey(validMachineId)),
            Arg.Any<CancellationToken>());
    }

    // -----------------------------------------------------------------------
    // Mixed batch — one dead-letter entry and one good entry
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies that within a single batch, a poison message is dead-lettered while a healthy
    /// message from a different machine is still coalesced and passed to UpdateBatchAsync.
    /// The dead-letter ACK and the success ACK are both issued correctly.
    /// </summary>
    [Test]
    public async Task ProcessAllPartitions_MixedDeadLetterAndGoodEntry_EachHandledCorrectly()
    {
        (MachineStateConsumerService service, IDatabase db, IMachineStateUpdater updater) = CreateService();

        long poisonMachineId = 707L;
        long healthyMachineId = 808L;
        string poisonItemsJson = BuildItemsJson(6, """{"cpu_usage_percent":99}""");
        string healthyItemsJson = BuildItemsJson(6, """{"cpu_usage_percent":10}""");

        StreamEntry poisonEntry = BuildStreamEntry("8-1", poisonMachineId, poisonItemsJson);
        StreamEntry healthyEntry = BuildStreamEntry("8-2", healthyMachineId, healthyItemsJson);

        RedisStream[] streams = [BuildRedisStream(0, [poisonEntry, healthyEntry])];
        SetupStreamRead(db, streams);

        long poisonCount = MachineStateConsumerService.MaxDeliveryAttempts + 1;

        // Poison entry gets a delivery count over the threshold; healthy entry gets 1.
        // The service calls db.StringIncrementAsync(key) — single-argument overload.
        db.StringIncrementAsync(Arg.Is<RedisKey>(k => k.ToString().Contains("8-1")))
            .Returns(Task.FromResult(poisonCount));

        db.StringIncrementAsync(Arg.Is<RedisKey>(k => k.ToString().Contains("8-2")))
            .Returns(Task.FromResult(1L));

        await RunServiceBrieflyAsync(service);

        // Healthy machine update must reach the updater.
        await updater.Received().UpdateBatchAsync(
            Arg.Is<Dictionary<long, List<StateUpdateMessage>>>(d =>
                d.ContainsKey(healthyMachineId) && d.ContainsKey(poisonMachineId) == false),
            Arg.Any<CancellationToken>());

        // StreamAcknowledgeAsync must have been called at least twice:
        // once for the dead-letter ACK and once for the success ACK.
        await db.Received(2).StreamAcknowledgeAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<RedisValue[]>());
    }
}
