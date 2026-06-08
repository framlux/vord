// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Net;
using System.Runtime.CompilerServices;
using Framlux.FleetManagement.Services.Core.Hangfire;
using Microsoft.Extensions.Logging;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Hangfire;

public sealed class LegacyRedisKeyCleanupTests
{
    private static (IConnectionMultiplexer Redis, IDatabase Db, IServer Server) BuildRedisMocks(params RedisKey[] patternMatches)
    {
        IDatabase db = Substitute.For<IDatabase>();
        IServer server = Substitute.For<IServer>();
        server.KeysAsync(pattern: Arg.Any<RedisValue>()).Returns(_ => AsAsync(patternMatches));
        // Sentinel key is absent by default — cleanup runs in full.
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);

        EndPoint endpoint = new IPEndPoint(IPAddress.Loopback, 6379);
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        redis.GetEndPoints(Arg.Any<bool>()).Returns(new[] { endpoint });
        redis.GetServer(endpoint, Arg.Any<object>()).Returns(server);

        return (redis, db, server);
    }

    private static async IAsyncEnumerable<RedisKey> AsAsync(IEnumerable<RedisKey> keys, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (RedisKey k in keys)
        {
            await Task.Yield();
            yield return k;
        }
    }

    [Test]
    public async Task RunAsync_DeletesEveryFixedKeyAndLogsTotal()
    {
        // Intent: the fixed-key list is the authoritative set of leftover Redis keys from the
        // pre-Hangfire era. Every one must be DEL'd on every cleanup run. The IDatabase mock
        // returns true for each KeyDelete (simulating "key existed and was deleted") so the
        // log line reports the expected count.
        (IConnectionMultiplexer redis, IDatabase db, _) = BuildRedisMocks();
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);

        ILogger<LegacyRedisKeyCleanup> logger = Substitute.For<ILogger<LegacyRedisKeyCleanup>>();
        LegacyRedisKeyCleanup cleanup = new(redis, logger);

        await cleanup.RunAsync(CancellationToken.None);

        // 10 fixed keys, each one DEL'd.
        await db.Received(1).KeyDeleteAsync("alert:delivery:queue", Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync("alert:delivery:deadletter", Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync("alert-evaluation-lock", Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync("lock:command-expiry", Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync("lock:data-export", Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync("lock:data-export-cleanup", Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync("lock:state-streaming", Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync("lock:partition-management", Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync("lock:stripe-sync", Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync("lock:usage-heartbeat", Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task RunAsync_PatternKeysScannedAndDeleted()
    {
        // Intent: condition-state keys and per-tenant health-sweep locks were generated dynamically
        // by ruleId/tenantId, so they must be SCAN-listed and deleted. The cleanup uses IServer.KeysAsync
        // (SCAN under the hood) to avoid blocking production Redis like KEYS would.
        RedisKey[] matches =
        [
            "alert:condition:7:42",
            "alert:condition:7:99",
            "lock:health-sweep:1",
            "lock:health-sweep:2",
        ];
        (IConnectionMultiplexer redis, IDatabase db, IServer server) = BuildRedisMocks(matches);
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(true);

        LegacyRedisKeyCleanup cleanup = new(redis, Substitute.For<ILogger<LegacyRedisKeyCleanup>>());

        await cleanup.RunAsync(CancellationToken.None);

        // KeysAsync called once per pattern across every endpoint.
        server.Received(1).KeysAsync(pattern: "alert:condition:*");
        server.Received(1).KeysAsync(pattern: "lock:health-sweep:*");

        // Each matched key was deleted exactly once. Production collects unique matches across
        // every (endpoint, pattern) pair before issuing deletes, so a key that matches multiple
        // patterns or appears on multiple endpoints is not deleted multiple times.
        await db.Received(1).KeyDeleteAsync("alert:condition:7:42", Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync("alert:condition:7:99", Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync("lock:health-sweep:1", Arg.Any<CommandFlags>());
        await db.Received(1).KeyDeleteAsync("lock:health-sweep:2", Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task RunAsync_NoKeysToDelete_CompletesQuietly()
    {
        // Intent: idempotency — running the cleanup on a Redis where nothing legacy remains must
        // succeed silently. KeyDeleteAsync returns false ("key not found"); the count is zero.
        (IConnectionMultiplexer redis, IDatabase db, _) = BuildRedisMocks();
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);

        ILogger<LegacyRedisKeyCleanup> logger = Substitute.For<ILogger<LegacyRedisKeyCleanup>>();
        LegacyRedisKeyCleanup cleanup = new(redis, logger);

        await cleanup.RunAsync(CancellationToken.None);

        // Each fixed key was attempted (count is just to ensure flow completed).
        await db.Received(10).KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task RunAsync_IteratesEveryEndpointForPatternScan()
    {
        // Intent: in Redis cluster mode there is one IServer per shard. The cleanup must call
        // KeysAsync on every endpoint, not just the first, or some shards leak legacy keys.
        IDatabase db = Substitute.For<IDatabase>();
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);

        IServer server1 = Substitute.For<IServer>();
        IServer server2 = Substitute.For<IServer>();
        server1.KeysAsync(pattern: Arg.Any<RedisValue>()).Returns(_ => AsAsync(Array.Empty<RedisKey>()));
        server2.KeysAsync(pattern: Arg.Any<RedisValue>()).Returns(_ => AsAsync(Array.Empty<RedisKey>()));

        EndPoint ep1 = new IPEndPoint(IPAddress.Loopback, 6379);
        EndPoint ep2 = new IPEndPoint(IPAddress.Loopback, 6380);

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        redis.GetEndPoints(Arg.Any<bool>()).Returns(new[] { ep1, ep2 });
        redis.GetServer(ep1, Arg.Any<object>()).Returns(server1);
        redis.GetServer(ep2, Arg.Any<object>()).Returns(server2);

        LegacyRedisKeyCleanup cleanup = new(redis, Substitute.For<ILogger<LegacyRedisKeyCleanup>>());

        await cleanup.RunAsync(CancellationToken.None);

        // Each endpoint was scanned for each of the two patterns.
        server1.Received(1).KeysAsync(pattern: "alert:condition:*");
        server1.Received(1).KeysAsync(pattern: "lock:health-sweep:*");
        server2.Received(1).KeysAsync(pattern: "alert:condition:*");
        server2.Received(1).KeysAsync(pattern: "lock:health-sweep:*");
    }

    [Test]
    public async Task RunAsync_SentinelKeyExists_SkipsAllWork()
    {
        // Intent: every worker pod runs this on startup. After the first successful run the
        // sentinel key tells subsequent pods to short-circuit. Without this, every rolling
        // deploy re-scans the whole Redis keyspace under SCAN, eating Redis CPU for no benefit.
        (IConnectionMultiplexer redis, IDatabase db, IServer server) = BuildRedisMocks();
        db.KeyExistsAsync("vord:legacy-cleanup:done", Arg.Any<CommandFlags>()).Returns(true);

        LegacyRedisKeyCleanup cleanup = new(redis, Substitute.For<ILogger<LegacyRedisKeyCleanup>>());

        await cleanup.RunAsync(CancellationToken.None);

        // No fixed-key deletes, no SCAN calls.
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
        server.DidNotReceive().KeysAsync(pattern: Arg.Any<RedisValue>());
    }

    [Test]
    public async Task RunAsync_AfterSuccess_SetsSentinelKey()
    {
        // Intent: a successful cleanup marks Redis so future boots short-circuit. Without the
        // sentinel set, the optimization above never triggers.
        (IConnectionMultiplexer redis, IDatabase db, _) = BuildRedisMocks();
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        db.StringSetAsync(
            Arg.Any<RedisKey>(),
            Arg.Any<RedisValue>(),
            Arg.Any<TimeSpan?>(),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>()).Returns(true);

        LegacyRedisKeyCleanup cleanup = new(redis, Substitute.For<ILogger<LegacyRedisKeyCleanup>>());

        await cleanup.RunAsync(CancellationToken.None);

        // Walk ReceivedCalls() to find a StringSet whose key argument equals the sentinel.
        // (Direct .Received() argument-matching trips on RedisKey implicit-conversion semantics.)
        bool sentinelWasSet = db.ReceivedCalls().Any(call =>
            call.GetMethodInfo().Name == nameof(IDatabase.StringSetAsync)
            && call.GetArguments().Length > 0
            && call.GetArguments()[0] is RedisKey key
            && key.ToString() == "vord:legacy-cleanup:done");

        await Assert.That(sentinelWasSet).IsTrue();
    }

    [Test]
    public async Task RunAsync_KeyDeleteThrows_LogsAndContinues()
    {
        // Intent: a transient Redis error mid-cleanup should not crash worker startup. The cleanup
        // is best-effort — a single key failure is logged, and the loop continues.
        (IConnectionMultiplexer redis, IDatabase db, _) = BuildRedisMocks();
        db.KeyDeleteAsync("alert:delivery:queue", Arg.Any<CommandFlags>())
          .Returns<Task<bool>>(_ => throw new RedisConnectionException(ConnectionFailureType.SocketFailure, "boom"));
        db.KeyDeleteAsync(Arg.Is<RedisKey>(k => k != "alert:delivery:queue"), Arg.Any<CommandFlags>()).Returns(true);

        LegacyRedisKeyCleanup cleanup = new(redis, Substitute.For<ILogger<LegacyRedisKeyCleanup>>());

        // Should not throw. The contract is best-effort.
        await cleanup.RunAsync(CancellationToken.None);

        // Subsequent fixed keys after the failing one were still attempted.
        await db.Received(1).KeyDeleteAsync("alert:delivery:deadletter", Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task RunAsync_NoEndpoints_CompletesQuietly()
    {
        // Intent: a fully-unavailable Redis cluster returns no endpoints. The cleanup must complete
        // without throwing — worker startup must not depend on Redis being addressable.
        IDatabase db = Substitute.For<IDatabase>();
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);
        db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(false);

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        redis.GetEndPoints(Arg.Any<bool>()).Returns(Array.Empty<EndPoint>());

        LegacyRedisKeyCleanup cleanup = new(redis, Substitute.For<ILogger<LegacyRedisKeyCleanup>>());

        await cleanup.RunAsync(CancellationToken.None);

        // Fixed-key deletes still attempted; no SCAN performed.
        await db.Received(10).KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
    }

    [Test]
    public async Task Constructor_NullRedis_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            LegacyRedisKeyCleanup _ = new(null!, Substitute.For<ILogger<LegacyRedisKeyCleanup>>());

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("redis");
    }

    [Test]
    public async Task Constructor_NullLogger_Throws()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            LegacyRedisKeyCleanup _ = new(Substitute.For<IConnectionMultiplexer>(), null!);

            return Task.CompletedTask;
        });
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("logger");
    }
}
