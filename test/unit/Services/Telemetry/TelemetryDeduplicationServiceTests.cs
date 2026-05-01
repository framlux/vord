// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using Framlux.FleetManagement.Server.Services.Telemetry;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Unit tests for <see cref="RedisTelemetryDeduplicationService"/>.
/// Uses NSubstitute to mock Redis interactions.
/// </summary>
public sealed class TelemetryDeduplicationServiceTests
{
    private static ServerConfigurationService CreateConfigService()
    {
        return new ServerConfigurationService(Substitute.For<IServerSettingsCache>(), Substitute.For<IConnectionMultiplexer>());
    }

    [Test]
    public async Task TryMarkSeenAsync_NewEvent_ReturnsTrue()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        db.StringSetAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<When>())
            .Returns(true);

        RedisTelemetryDeduplicationService service = new(redis, CreateConfigService());

        bool result = await service.TryMarkSeenAsync("new-event-1");

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task TryMarkSeenAsync_DuplicateEvent_ReturnsFalse()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        db.StringSetAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<When>())
            .Returns(false);

        RedisTelemetryDeduplicationService service = new(redis, CreateConfigService());

        bool result = await service.TryMarkSeenAsync("dup-event-1");

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task TryMarkSeenAsync_UsesCorrectKeyPrefix()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        db.StringSetAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<When>())
            .Returns(true);

        RedisTelemetryDeduplicationService service = new(redis, CreateConfigService());

        await service.TryMarkSeenAsync("test-event");

        // Verify the key includes the prefix.
        await db.Received(1).StringSetAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "telemetry:dedup:test-event"),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(),
            When.NotExists);
    }

    [Test]
    public async Task TryMarkSeenAsync_AppliesTtl()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        db.StringSetAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<When>())
            .Returns(true);

        RedisTelemetryDeduplicationService service = new(redis, CreateConfigService());

        await service.TryMarkSeenAsync("ttl-event");

        // Verify TTL is applied (default: 5 minutes).
        await db.Received(1).StringSetAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Is<TimeSpan?>(t => t.HasValue && t.Value == TimeSpan.FromMinutes(5)),
            When.NotExists);
    }

    [Test]
    public async Task TryMarkSeenBatchAsync_MixedResults_ReturnsCorrectMap()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        IBatch batch = Substitute.For<IBatch>();
        db.CreateBatch(Arg.Any<object>()).Returns(batch);

        // First event is new, second is a duplicate.
        batch.StringSetAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "telemetry:dedup:event-a"),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<When>())
            .Returns(Task.FromResult(true));

        batch.StringSetAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "telemetry:dedup:event-b"),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<When>())
            .Returns(Task.FromResult(false));

        RedisTelemetryDeduplicationService service = new(redis, CreateConfigService());

        Dictionary<string, bool> result = await service.TryMarkSeenBatchAsync(["event-a", "event-b"]);

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result["event-a"]).IsTrue();
        await Assert.That(result["event-b"]).IsFalse();
    }

    [Test]
    public async Task TryMarkSeenBatchAsync_EmptyInput_ReturnsEmptyDictionary()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        IBatch batch = Substitute.For<IBatch>();
        db.CreateBatch(Arg.Any<object>()).Returns(batch);

        RedisTelemetryDeduplicationService service = new(redis, CreateConfigService());

        Dictionary<string, bool> result = await service.TryMarkSeenBatchAsync([]);

        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TryMarkSeenAsync_EmptyEventId_StillCallsRedis()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        db.StringSetAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<When>())
            .Returns(true);

        RedisTelemetryDeduplicationService service = new(redis, CreateConfigService());

        bool result = await service.TryMarkSeenAsync("");

        // Empty string event ID is treated as a valid key.
        await Assert.That(result).IsTrue();
        await db.Received(1).StringSetAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "telemetry:dedup:"),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(),
            When.NotExists);
    }

    [Test]
    public async Task Constructor_NullRedis_ThrowsArgumentNullException()
    {
        await Assert.That(() => new RedisTelemetryDeduplicationService(null!, CreateConfigService()))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Constructor_NullConfigService_ThrowsArgumentNullException()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();

        await Assert.That(() => new RedisTelemetryDeduplicationService(redis, null!))
            .Throws<ArgumentNullException>();
    }
}
