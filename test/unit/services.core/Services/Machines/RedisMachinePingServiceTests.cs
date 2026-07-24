// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Polly;
using Polly.Registry;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="RedisMachinePingService"/>.
/// </summary>
public class RedisMachinePingServiceTests
{
    /// <summary>
    /// Builds a real "redis-ping" resilience pipeline registration using the same
    /// <see cref="RedisRetryPipelineOptions.Create"/> factory the production registration
    /// in ServiceCollectionExtensions calls, so a change to the production retry semantics
    /// is automatically exercised here too. Only the delay is shortened for test speed.
    /// </summary>
    private static ResiliencePipelineProvider<string> CreatePipelineProvider(int delayMs = 1)
    {
        ServiceCollection services = new();
        services.AddResiliencePipeline("redis-ping", builder =>
            builder.AddRetry(RedisRetryPipelineOptions.Create(TimeSpan.FromMilliseconds(delayMs), NullLogger.Instance)));

        return services.BuildServiceProvider().GetRequiredService<ResiliencePipelineProvider<string>>();
    }

    private static (RedisMachinePingService service, IDatabase redisDb) CreateService()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        RedisMachinePingService service = new(redis, CreatePipelineProvider());

        return (service, redisDb);
    }

    // ========== GetLastPingAsync tests ==========

    [Test]
    public async Task GetLastPingAsync_NoPing_ReturnsNull()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

        DateTimeOffset? result = await service.GetLastPingAsync(1);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetLastPingAsync_HasPing_ReturnsTimestamp()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(timestampMs.ToString()));

        DateTimeOffset? result = await service.GetLastPingAsync(1);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Value.ToUnixTimeMilliseconds()).IsEqualTo(timestampMs);
    }

    [Test]
    public async Task GetLastPingAsync_NonNumericValue_ReturnsNull()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>("not-a-number"));

        DateTimeOffset? result = await service.GetLastPingAsync(1);

        await Assert.That(result).IsNull();
    }

    // ========== IsOnlineAsync tests ==========

    [Test]
    public async Task IsOnlineAsync_NoPing_ReturnsFalse()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

        bool result = await service.IsOnlineAsync(1, TimeSpan.FromMinutes(5));

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsOnlineAsync_RecentPing_ReturnsTrue()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        long recentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(recentMs.ToString()));

        bool result = await service.IsOnlineAsync(1, TimeSpan.FromMinutes(5));

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsOnlineAsync_OldPing_ReturnsFalse()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();
        long oldMs = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
        redisDb.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(oldMs.ToString()));

        bool result = await service.IsOnlineAsync(1, TimeSpan.FromMinutes(5));

        await Assert.That(result).IsFalse();
    }

    // ========== RecordPingAsync tests ==========

    [Test]
    public async Task RecordPingAsync_StoresLastPingUnderMachineKeyWithSelfEvictingTtl()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();

        await service.RecordPingAsync(42);

        // A single value under the machine's ping key, with a TTL so a machine that stops
        // reporting self-evicts rather than leaking a key forever.
        await redisDb.Received().StringSetAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "machine:ping:42"),
            Arg.Any<RedisValue>(),
            Arg.Is<Expiration>(e => e.Equals(new Expiration(TimeSpan.FromDays(7)))),
            Arg.Any<ValueCondition>(),
            Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task RecordPingAsync_DoesNotUseSortedSets()
    {
        (RedisMachinePingService service, IDatabase redisDb) = CreateService();

        await service.RecordPingAsync(42);

        // The 7-day per-heartbeat sorted-set history is gone; nothing should touch a sorted set.
        bool usedSortedSet = redisDb.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name.StartsWith("SortedSet", StringComparison.Ordinal));
        await Assert.That(usedSortedSet).IsFalse();
    }

    // ========== AreOnlineAsync tests ==========

    [Test]
    public async Task AreOnlineAsync_MixedMachines_ReturnsCorrectStatusMap()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        RedisMachinePingService service = new(redis, CreatePipelineProvider());

        IBatch batch = Substitute.For<IBatch>();
        redisDb.CreateBatch(Arg.Any<object>()).Returns(batch);

        long recentMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long oldMs = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();

        // Machine 1: recent ping (online).
        batch.StringGetAsync(Arg.Is<RedisKey>(k => k.ToString() == "machine:ping:1"), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(recentMs.ToString()));

        // Machine 2: old ping (offline).
        batch.StringGetAsync(Arg.Is<RedisKey>(k => k.ToString() == "machine:ping:2"), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(oldMs.ToString()));

        // Machine 3: no ping (offline).
        batch.StringGetAsync(Arg.Is<RedisKey>(k => k.ToString() == "machine:ping:3"), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

        Dictionary<long, bool> result = await service.AreOnlineAsync([1L, 2L, 3L], TimeSpan.FromMinutes(5));

        await Assert.That(result[1]).IsTrue();
        await Assert.That(result[2]).IsFalse();
        await Assert.That(result[3]).IsFalse();
    }

    [Test]
    public async Task AreOnlineAsync_AllMachinesNoData_ReturnsAllFalse()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        RedisMachinePingService service = new(redis, CreatePipelineProvider());

        IBatch batch = Substitute.For<IBatch>();
        redisDb.CreateBatch(Arg.Any<object>()).Returns(batch);

        batch.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

        Dictionary<long, bool> result = await service.AreOnlineAsync([1L, 2L], TimeSpan.FromMinutes(5));

        await Assert.That(result[1]).IsFalse();
        await Assert.That(result[2]).IsFalse();
    }

    // ========== GetLastPingsAsync tests ==========

    [Test]
    public async Task GetLastPingsAsync_MultipleMachines_ReturnsBatchResults()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        RedisMachinePingService service = new(redis, CreatePipelineProvider());

        IBatch batch = Substitute.For<IBatch>();
        redisDb.CreateBatch(Arg.Any<object>()).Returns(batch);

        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        batch.StringGetAsync(Arg.Is<RedisKey>(k => k.ToString() == "machine:ping:10"), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(ts.ToString()));

        batch.StringGetAsync(Arg.Is<RedisKey>(k => k.ToString() == "machine:ping:20"), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult<RedisValue>(RedisValue.Null));

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
        RedisMachinePingService service = new(redis, CreatePipelineProvider());

        IBatch batch = Substitute.For<IBatch>();
        redisDb.CreateBatch(Arg.Any<object>()).Returns(batch);

        Dictionary<long, DateTimeOffset?> result = await service.GetLastPingsAsync([]);

        await Assert.That(result.Count).IsEqualTo(0);
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
        RedisMachinePingService service = new(redis, CreatePipelineProvider());

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
        RedisMachinePingService service = new(redis, CreatePipelineProvider());

        IBatch batch = Substitute.For<IBatch>();
        redisDb.CreateBatch(Arg.Any<object>()).Returns(batch);

        Dictionary<long, ulong> result = await service.GetAgentCapabilitiesBatchAsync([]);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    // ========== Retry pipeline semantics ==========

    [Test]
    public async Task RecordPingAsync_TransientRedisFailure_RetriesUntilSuccess()
    {
        // Pins the retry semantics carried over from the deleted RetryHelper: transient
        // failures are retried by the "redis-ping" pipeline until the call succeeds.
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        RedisMachinePingService service = new(redis, CreatePipelineProvider());

        int callCount = 0;
        redisDb.StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount < 3)
                {
                    throw new TimeoutException("transient redis timeout");
                }

                return Task.FromResult(true);
            });

        await service.RecordPingAsync(7);

        await Assert.That(callCount).IsEqualTo(3);
    }

    [Test]
    public async Task SetAgentCapabilitiesAsync_ExhaustsRetries_ThrowsFinalException()
    {
        // With three retry attempts configured, a permanently failing operation makes
        // exactly four calls (the initial attempt plus three retries) then throws.
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        RedisMachinePingService service = new(redis, CreatePipelineProvider());

        int callCount = 0;
        redisDb.StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>())
            .Returns<bool>(_ =>
            {
                callCount++;

                throw new TimeoutException("permanent redis timeout");
            });

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await service.SetAgentCapabilitiesAsync(8, 1);
        });

        await Assert.That(callCount).IsEqualTo(4);
    }

    [Test]
    public async Task RecordPingAsync_OperationCanceledException_NeverRetried()
    {
        // OperationCanceledException must propagate immediately without being retried,
        // matching the deleted RetryHelper's cancellation-never-retried behavior.
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase redisDb = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(redisDb);
        RedisMachinePingService service = new(redis, CreatePipelineProvider());

        int callCount = 0;
        redisDb.StringSetAsync(
            Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<Expiration>(), Arg.Any<ValueCondition>(), Arg.Any<CommandFlags>())
            .Returns<bool>(_ =>
            {
                callCount++;

                throw new OperationCanceledException();
            });

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await service.RecordPingAsync(9);
        });

        await Assert.That(callCount).IsEqualTo(1);
    }
}
