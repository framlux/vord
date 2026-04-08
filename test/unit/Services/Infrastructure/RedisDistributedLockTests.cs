// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Infrastructure;
using NSubstitute;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>
/// Tests for <see cref="RedisDistributedLock"/>.
/// </summary>
public class RedisDistributedLockTests
{
    private static (RedisDistributedLock lockService, IDatabase db) CreateLockService()
    {
        IDatabase db = Substitute.For<IDatabase>();
        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        RedisDistributedLock lockService = new(redis);

        return (lockService, db);
    }

    /// <summary>
    /// Verifies that acquiring an available lock returns a non-null handle.
    /// </summary>
    [Test]
    public async Task TryAcquireAsync_LockAvailable_ReturnsHandle()
    {
        (RedisDistributedLock lockService, IDatabase db) = CreateLockService();
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Returns(true);

        LockHandle? handle = await lockService.TryAcquireAsync("test-lock", TimeSpan.FromSeconds(30));

        await Assert.That(handle).IsNotNull();
    }

    /// <summary>
    /// Verifies that attempting to acquire a held lock returns null.
    /// </summary>
    [Test]
    public async Task TryAcquireAsync_LockHeld_ReturnsNull()
    {
        (RedisDistributedLock lockService, IDatabase db) = CreateLockService();
        db.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(), Arg.Any<When>())
            .Returns(false);

        LockHandle? handle = await lockService.TryAcquireAsync("test-lock", TimeSpan.FromSeconds(30));

        await Assert.That(handle).IsNull();
    }

    /// <summary>
    /// Verifies that disposing a lock handle releases the lock via Lua script
    /// with the correct key and owner token arguments.
    /// </summary>
    [Test]
    public async Task DisposeAsync_ReleasesLock()
    {
        (RedisDistributedLock lockService, IDatabase db) = CreateLockService();

        RedisValue capturedToken = RedisValue.Null;
        db.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Do<RedisValue>(v => capturedToken = v),
                Arg.Any<TimeSpan?>(),
                Arg.Any<When>())
            .Returns(true);

        LockHandle? handle = await lockService.TryAcquireAsync("test-lock", TimeSpan.FromSeconds(30));
        await handle!.DisposeAsync();

        await db.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Is<RedisKey[]>(keys => keys.Length == 1 && keys[0] == (RedisKey)"test-lock"),
            Arg.Is<RedisValue[]>(vals => vals.Length == 1 && vals[0] == capturedToken),
            Arg.Any<CommandFlags>());
    }
}
