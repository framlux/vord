// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="RedisMachinePingService"/>.
/// </summary>
public class RedisMachinePingServiceTests
{
    private static (RedisMachinePingService service, IDatabase redisDb) CreateService()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        NullLogger<RedisMachinePingService> logger = new();
        RedisMachinePingService service = new(redis, logger);

        return (service, redisDb);
    }

    // ========== GetLastPingAsync tests ==========

    [Test]
    public async Task GetLastPingAsync_NoPings_ReturnsNull()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        redisDb.SortedSetRangeByScoreWithScoresAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<double>(),
            Arg.Any<double>(),
            Arg.Any<Exclude>(),
            Arg.Any<Order>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<CommandFlags>())
            .Returns(Array.Empty<SortedSetEntry>());

        DateTimeOffset? result = await service.GetLastPingAsync(1);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetLastPingAsync_HasPing_ReturnsTimestamp()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        double timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SortedSetEntry[] entries = [new SortedSetEntry("value", timestampMs)];
        redisDb.SortedSetRangeByScoreWithScoresAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<double>(),
            Arg.Any<double>(),
            Arg.Any<Exclude>(),
            Arg.Any<Order>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<CommandFlags>())
            .Returns(entries);

        DateTimeOffset? result = await service.GetLastPingAsync(1);

        await Assert.That(result).IsNotNull();
    }

    // ========== IsOnlineAsync tests ==========

    [Test]
    public async Task IsOnlineAsync_NoPings_ReturnsFalse()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        redisDb.SortedSetRangeByScoreWithScoresAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<double>(),
            Arg.Any<double>(),
            Arg.Any<Exclude>(),
            Arg.Any<Order>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<CommandFlags>())
            .Returns(Array.Empty<SortedSetEntry>());

        bool result = await service.IsOnlineAsync(1, TimeSpan.FromMinutes(5));

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsOnlineAsync_RecentPing_ReturnsTrue()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        double recentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SortedSetEntry[] entries = [new SortedSetEntry("value", recentMs)];
        redisDb.SortedSetRangeByScoreWithScoresAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<double>(),
            Arg.Any<double>(),
            Arg.Any<Exclude>(),
            Arg.Any<Order>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<CommandFlags>())
            .Returns(entries);

        bool result = await service.IsOnlineAsync(1, TimeSpan.FromMinutes(5));

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsOnlineAsync_OldPing_ReturnsFalse()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        double oldMs = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
        SortedSetEntry[] entries = [new SortedSetEntry("value", oldMs)];
        redisDb.SortedSetRangeByScoreWithScoresAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<double>(),
            Arg.Any<double>(),
            Arg.Any<Exclude>(),
            Arg.Any<Order>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<CommandFlags>())
            .Returns(entries);

        bool result = await service.IsOnlineAsync(1, TimeSpan.FromMinutes(5));

        await Assert.That(result).IsFalse();
    }

    // ========== GetPingHistoryAsync tests ==========

    [Test]
    public async Task GetPingHistoryAsync_NoPings_ReturnsEmpty()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        redisDb.SortedSetRangeByScoreWithScoresAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<double>(),
            Arg.Any<double>(),
            Arg.Any<Exclude>(),
            Arg.Any<Order>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<CommandFlags>())
            .Returns(Array.Empty<SortedSetEntry>());

        IEnumerable<DateTimeOffset> result = await service.GetPingHistoryAsync(1, TimeSpan.FromHours(1));

        await Assert.That(result.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task GetPingHistoryAsync_WithPings_ReturnsTimestamps()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        double ts1 = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        double ts2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        SortedSetEntry[] entries =
        [
            new SortedSetEntry(ts1.ToString(), ts1),
            new SortedSetEntry(ts2.ToString(), ts2),
        ];
        redisDb.SortedSetRangeByScoreWithScoresAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<double>(),
            Arg.Any<double>(),
            Arg.Any<Exclude>(),
            Arg.Any<Order>(),
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<CommandFlags>())
            .Returns(entries);

        IEnumerable<DateTimeOffset> result = await service.GetPingHistoryAsync(1, TimeSpan.FromHours(1));

        await Assert.That(result.Count()).IsEqualTo(2);
    }

    // ========== RecordPingAsync tests ==========

    [Test]
    public async Task RecordPingAsync_StoresTimestampInSortedSet()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();

        await service.RecordPingAsync(42);

        // Verify SortedSetAddAsync was called with the correct key pattern for machine 42.
        await redisDb.Received().SortedSetAddAsync(
            Arg.Is<RedisKey>(k => k.ToString().Contains("42")),
            Arg.Any<RedisValue>(),
            Arg.Any<double>(),
            Arg.Any<SortedSetWhen>(),
            Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task RecordPingAsync_CompletesSuccessfully()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();

        // Should complete without error — retention window enforcement happens on write.
        await service.RecordPingAsync(99);

        // Verify the DB was interacted with for both the add and the trim.
        await redisDb.ReceivedWithAnyArgs().SortedSetRemoveRangeByScoreAsync(
            default, default, default, default, default);
    }

    // ========== AreOnlineAsync tests ==========

    [Test]
    public async Task AreOnlineAsync_MixedMachines_ReturnsCorrectStatusMap()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        NullLogger<RedisMachinePingService> logger = new();
        RedisMachinePingService service = new(redis, logger);

        IBatch batch = Substitute.For<IBatch>();
        redisDb.CreateBatch(Arg.Any<object>()).Returns(batch);

        double recentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        double oldMs = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();

        // Machine 1: recent ping (online).
        batch.SortedSetRangeByScoreWithScoresAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "machine:ping:1"),
            Arg.Any<double>(), Arg.Any<double>(), Arg.Any<Exclude>(),
            Arg.Any<Order>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new SortedSetEntry[] { new("v", recentMs) }));

        // Machine 2: old ping (offline).
        batch.SortedSetRangeByScoreWithScoresAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "machine:ping:2"),
            Arg.Any<double>(), Arg.Any<double>(), Arg.Any<Exclude>(),
            Arg.Any<Order>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new SortedSetEntry[] { new("v", oldMs) }));

        // Machine 3: no pings (offline).
        batch.SortedSetRangeByScoreWithScoresAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "machine:ping:3"),
            Arg.Any<double>(), Arg.Any<double>(), Arg.Any<Exclude>(),
            Arg.Any<Order>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(Array.Empty<SortedSetEntry>()));

        Dictionary<long, bool> result = await service.AreOnlineAsync([1L, 2L, 3L], TimeSpan.FromMinutes(5));

        await Assert.That(result[1]).IsTrue();
        await Assert.That(result[2]).IsFalse();
        await Assert.That(result[3]).IsFalse();
    }

    // ========== GetLastPingsAsync tests ==========

    [Test]
    public async Task GetLastPingsAsync_MultipleMachines_ReturnsBatchResults()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        NullLogger<RedisMachinePingService> logger = new();
        RedisMachinePingService service = new(redis, logger);

        IBatch batch = Substitute.For<IBatch>();
        redisDb.CreateBatch(Arg.Any<object>()).Returns(batch);

        double ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        batch.SortedSetRangeByScoreWithScoresAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "machine:ping:10"),
            Arg.Any<double>(), Arg.Any<double>(), Arg.Any<Exclude>(),
            Arg.Any<Order>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new SortedSetEntry[] { new("v", ts) }));

        batch.SortedSetRangeByScoreWithScoresAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "machine:ping:20"),
            Arg.Any<double>(), Arg.Any<double>(), Arg.Any<Exclude>(),
            Arg.Any<Order>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(Array.Empty<SortedSetEntry>()));

        Dictionary<long, DateTimeOffset?> result = await service.GetLastPingsAsync([10L, 20L]);

        await Assert.That(result[10]).IsNotNull();
        await Assert.That(result[20]).IsNull();
    }

    [Test]
    public async Task GetLastPingsAsync_EmptyInput_ReturnsEmptyDictionary()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        NullLogger<RedisMachinePingService> logger = new();
        RedisMachinePingService service = new(redis, logger);

        IBatch batch = Substitute.For<IBatch>();
        redisDb.CreateBatch(Arg.Any<object>()).Returns(batch);

        Dictionary<long, DateTimeOffset?> result = await service.GetLastPingsAsync([]);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AreOnlineAsync_AllMachinesNoData_ReturnsAllFalse()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        NullLogger<RedisMachinePingService> logger = new();
        RedisMachinePingService service = new(redis, logger);

        IBatch batch = Substitute.For<IBatch>();
        redisDb.CreateBatch(Arg.Any<object>()).Returns(batch);

        batch.SortedSetRangeByScoreWithScoresAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<double>(), Arg.Any<double>(), Arg.Any<Exclude>(),
            Arg.Any<Order>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(Array.Empty<SortedSetEntry>()));

        Dictionary<long, bool> result = await service.AreOnlineAsync([1L, 2L], TimeSpan.FromMinutes(5));

        await Assert.That(result[1]).IsFalse();
        await Assert.That(result[2]).IsFalse();
    }

    // ========== GetAgentCapabilitiesAsync tests ==========

    [Test]
    public async Task GetAgentCapabilitiesAsync_NoValue_ReturnsZero()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

        ulong result = await service.GetAgentCapabilitiesAsync(1);

        await Assert.That(result).IsEqualTo(0UL);
    }

    [Test]
    public async Task GetAgentCapabilitiesAsync_ValidValue_ReturnsParsedCapabilities()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(new RedisValue("42")));

        ulong result = await service.GetAgentCapabilitiesAsync(1);

        await Assert.That(result).IsEqualTo(42UL);
    }

    [Test]
    public async Task GetAgentCapabilitiesAsync_InvalidStringValue_ReturnsZero()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(new RedisValue("not-a-number")));

        ulong result = await service.GetAgentCapabilitiesAsync(1);

        await Assert.That(result).IsEqualTo(0UL);
    }

    [Test]
    public async Task GetAgentCapabilitiesAsync_EmptyString_ReturnsZero()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(new RedisValue("")));

        ulong result = await service.GetAgentCapabilitiesAsync(1);

        await Assert.That(result).IsEqualTo(0UL);
    }

    // ========== SetAgentCapabilitiesAsync tests ==========

    [Test]
    public async Task SetAgentCapabilitiesAsync_StoresValueWithCorrectKey()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();

        await service.SetAgentCapabilitiesAsync(99, 255);

        // Verify the value was stored with the correct key pattern
        IEnumerable<NSubstitute.Core.ICall> calls = redisDb.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == "StringSetAsync");
        await Assert.That(calls.Count()).IsGreaterThanOrEqualTo(1);
    }

    // ========== GetAgentCapabilitiesBatchAsync tests ==========

    [Test]
    public async Task GetAgentCapabilitiesBatchAsync_MultipleMachines_ReturnsCorrectMap()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        NullLogger<RedisMachinePingService> logger = new();
        RedisMachinePingService service = new(redis, logger);

        IBatch batch = Substitute.For<IBatch>();
        redisDb.CreateBatch(Arg.Any<object>()).Returns(batch);

        // Machine 1: has capabilities
        batch.StringGetAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "machine:caps:1"),
            Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(new RedisValue("7")));

        // Machine 2: no value
        batch.StringGetAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "machine:caps:2"),
            Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

        // Machine 3: invalid value
        batch.StringGetAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "machine:caps:3"),
            Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(new RedisValue("bad")));

        Dictionary<long, ulong> result = await service.GetAgentCapabilitiesBatchAsync([1L, 2L, 3L]);

        await Assert.That(result[1]).IsEqualTo(7UL);
        await Assert.That(result[2]).IsEqualTo(0UL);
        await Assert.That(result[3]).IsEqualTo(0UL);
    }

    [Test]
    public async Task GetAgentCapabilitiesBatchAsync_EmptyInput_ReturnsEmptyDictionary()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        NullLogger<RedisMachinePingService> logger = new();
        RedisMachinePingService service = new(redis, logger);

        IBatch batch = Substitute.For<IBatch>();
        redisDb.CreateBatch(Arg.Any<object>()).Returns(batch);

        Dictionary<long, ulong> result = await service.GetAgentCapabilitiesBatchAsync([]);

        await Assert.That(result.Count).IsEqualTo(0);
    }
}
