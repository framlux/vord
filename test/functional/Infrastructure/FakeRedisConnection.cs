// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using NSubstitute;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace Framlux.FleetManagement.FunctionalTest.Infrastructure;

/// <summary>
/// Creates a fake <see cref="IConnectionMultiplexer"/> backed by an in-memory dictionary.
/// Uses NSubstitute to mock the Redis interfaces with functional string get/set/delete operations.
/// </summary>
public static class FakeRedisConnection
{
    /// <summary>
    /// Creates a fake <see cref="IConnectionMultiplexer"/> with in-memory string operations.
    /// </summary>
    /// <returns>A configured fake Redis connection.</returns>
    public static IConnectionMultiplexer Create()
    {
        ConcurrentDictionary<string, string> store = new();

        IDatabase db = Substitute.For<IDatabase>();

        // StringGetAsync(RedisKey, CommandFlags)
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                string key = callInfo.ArgAt<RedisKey>(0).ToString();
                if (store.TryGetValue(key, out string? val))
                {
                    return (RedisValue)val;
                }

                return RedisValue.Null;
            });

        // StringSetAsync(RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags)
        db.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(),
                Arg.Any<When>(),
                Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                string key = callInfo.ArgAt<RedisKey>(0).ToString();
                string value = callInfo.ArgAt<RedisValue>(1).ToString();
                When when = callInfo.ArgAt<When>(4);

                if (when == When.NotExists && store.ContainsKey(key))
                {
                    return false;
                }

                store[key] = value;

                return true;
            });

        // KeyDeleteAsync(RedisKey, CommandFlags)
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                string key = callInfo.ArgAt<RedisKey>(0).ToString();

                return store.TryRemove(key, out _);
            });

        // KeyExpireAsync — always succeeds
        db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<ExpireWhen>(), Arg.Any<CommandFlags>())
            .Returns(true);

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        redis.IsConnected.Returns(true);

        return redis;
    }
}
