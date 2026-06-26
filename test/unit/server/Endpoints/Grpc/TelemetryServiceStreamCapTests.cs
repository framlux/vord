// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Services.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Polly;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Endpoints.Grpc;

/// <summary>
/// M1 + M2 regression tests for <see cref="TelemetryService"/>. Exercises the
/// internal-static slot helpers (<c>TryAcquireStreamSlotAsync</c>, <c>ReleaseStreamSlotAsync</c>)
/// directly so the Redis-key shape, cap math, and Redis-outage fail-closed behavior are pinned
/// without needing a real Redis or a full streaming functional setup.
/// </summary>
public sealed class TelemetryServiceStreamCapTests
{
    private static TelemetryService BuildService(
        IConnectionMultiplexer redis, TelemetryOptions options, ProcessStreamSlotLimiter? processLimiter = null)
    {
        return new TelemetryService(
            Substitute.For<IServiceScopeFactory>(),
            Substitute.For<ITelemetryDeduplicationService>(),
            Substitute.For<ISubscriptionService>(),
            Substitute.For<IEventAlertService>(),
            ResiliencePipeline.Empty,
            redis,
            Options.Create(options),
            processLimiter ?? new ProcessStreamSlotLimiter(5000),
            NullLogger<TelemetryService>.Instance);
    }

    [Test]
    public async Task TryAcquireStreamSlot_FirstStreamUnderCap_ReturnsTrue()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(1L);

        TelemetryService svc = BuildService(redis, new TelemetryOptions { MaxConcurrentStreamsPerMachine = 1 });

        StreamSlotSource? acquired = await svc.TryAcquireStreamSlotAsync(42, TimeSpan.FromMinutes(6));

        await Assert.That(acquired).IsEqualTo(StreamSlotSource.Redis);
        await db.Received(1).KeyExpireAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "telemetry:stream:42"),
            Arg.Any<TimeSpan?>(),
            Arg.Any<ExpireWhen>(),
            Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task TryAcquireStreamSlot_OverCap_ReturnsFalse_AndDecrements()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        // INCR returns 2 — already at cap; the slot must be rejected and DECR'd.
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(2L);
        db.StringDecrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(1L);

        TelemetryService svc = BuildService(redis, new TelemetryOptions { MaxConcurrentStreamsPerMachine = 1 });

        StreamSlotSource? acquired = await svc.TryAcquireStreamSlotAsync(42, TimeSpan.FromMinutes(6));

        await Assert.That(acquired).IsNull();
        await db.Received(1).StringDecrementAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "telemetry:stream:42"),
            Arg.Any<long>(),
            Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task TryAcquireStreamSlot_CapOfTwo_AllowsTwo()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        // Returns 1 then 2 to simulate two sequential acquires.
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(1L, 2L);

        TelemetryService svc = BuildService(redis, new TelemetryOptions { MaxConcurrentStreamsPerMachine = 2 });

        StreamSlotSource? first = await svc.TryAcquireStreamSlotAsync(42, TimeSpan.FromMinutes(6));
        StreamSlotSource? second = await svc.TryAcquireStreamSlotAsync(42, TimeSpan.FromMinutes(6));

        await Assert.That(first).IsEqualTo(StreamSlotSource.Redis);
        await Assert.That(second).IsEqualTo(StreamSlotSource.Redis);
    }

    [Test]
    public async Task TryAcquireStreamSlot_RedisException_FailsClosedToProcessCap()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns<Task<long>>(_ => throw new RedisException("connection lost"));

        // A per-process ceiling of 1: the first acquire during the outage falls back to the
        // process limiter, the second must be rejected — proving the cap fails closed, not open.
        ProcessStreamSlotLimiter processLimiter = new(maxPerProcess: 1);
        TelemetryService svc = BuildService(
            redis, new TelemetryOptions { MaxConcurrentStreamsPerMachine = 1 }, processLimiter);

        StreamSlotSource? first = await svc.TryAcquireStreamSlotAsync(42, TimeSpan.FromMinutes(6));
        StreamSlotSource? second = await svc.TryAcquireStreamSlotAsync(43, TimeSpan.FromMinutes(6));

        await Assert.That(first).IsEqualTo(StreamSlotSource.Process);
        await Assert.That(second).IsNull();
    }

    [Test]
    public async Task ReleaseStreamSlot_CountReachesZero_DeletesKey()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        db.StringDecrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(0L);

        TelemetryService svc = BuildService(redis, new TelemetryOptions());

        await svc.ReleaseStreamSlotAsync(42, StreamSlotSource.Redis);

        await db.Received(1).KeyDeleteAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "telemetry:stream:42"),
            Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task ReleaseStreamSlot_CountStillPositive_DoesNotDeleteKey()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        db.StringDecrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(1L);

        TelemetryService svc = BuildService(redis, new TelemetryOptions());

        await svc.ReleaseStreamSlotAsync(42, StreamSlotSource.Redis);

        await db.DidNotReceive().KeyDeleteAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task ReleaseStreamSlot_RedisException_DoesNotThrow()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        db.StringDecrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns<Task<long>>(_ => throw new RedisException("connection lost"));

        TelemetryService svc = BuildService(redis, new TelemetryOptions());

        Exception? caught = null;
        try
        {
            await svc.ReleaseStreamSlotAsync(42, StreamSlotSource.Redis);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsNull();
    }

    [Test]
    public async Task TryAcquireStreamSlot_DifferentMachines_HaveIndependentSlots()
    {
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        db.StringIncrementAsync(Arg.Any<RedisKey>(), Arg.Any<long>(), Arg.Any<CommandFlags>())
            .Returns(1L);

        TelemetryService svc = BuildService(redis, new TelemetryOptions { MaxConcurrentStreamsPerMachine = 1 });

        StreamSlotSource? a = await svc.TryAcquireStreamSlotAsync(1, TimeSpan.FromMinutes(6));
        StreamSlotSource? b = await svc.TryAcquireStreamSlotAsync(2, TimeSpan.FromMinutes(6));

        await Assert.That(a).IsEqualTo(StreamSlotSource.Redis);
        await Assert.That(b).IsEqualTo(StreamSlotSource.Redis);
        // Distinct keys used.
        await db.Received(1).StringIncrementAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "telemetry:stream:1"),
            Arg.Any<long>(),
            Arg.Any<CommandFlags>());
        await db.Received(1).StringIncrementAsync(
            Arg.Is<RedisKey>(k => k.ToString() == "telemetry:stream:2"),
            Arg.Any<long>(),
            Arg.Any<CommandFlags>());
    }
}
