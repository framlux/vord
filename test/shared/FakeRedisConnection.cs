// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using NSubstitute;
using StackExchange.Redis;
using System.Collections.Concurrent;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// Creates a fake <see cref="IConnectionMultiplexer"/> backed by an in-memory dictionary.
/// Uses NSubstitute to mock the Redis interfaces with functional string get/set/delete operations.
/// <para>
/// Every <c>StringSetAsync</c> overload the production code can bind to is stubbed against the same
/// backing store. This matters more than it looks: a call like
/// <c>StringSetAsync(key, value, ttl)</c> binds to the <see cref="Expiration"/> overload, not the
/// <c>TimeSpan?</c> one, so stubbing a single overload leaves the others returning NSubstitute's
/// default. An unstubbed set is silent — it stores nothing and returns false — and the following
/// get then misses, so a test exercising a cache-backed path quietly exercises the cache-miss path
/// instead and still passes. Any new overload used by production must be added here.
/// </para>
/// <para>
/// Existence conditions (<see cref="When"/> and <see cref="ValueCondition"/>) are honoured, because
/// callers such as the OIDC nonce replay guard depend on a second set returning false. Expiry is
/// deliberately NOT simulated: entries live for the lifetime of the fake. A test that needs to
/// observe expiry should delete the key explicitly rather than wait, so nothing here depends on
/// wall-clock time.
/// </para>
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
            .Returns(callInfo => ReadValue(store, callInfo.ArgAt<RedisKey>(0)));

        // StringGetAsync(RedisKey[], CommandFlags)
        db.StringGetAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                RedisKey[] keys = callInfo.ArgAt<RedisKey[]>(0);

                return Array.ConvertAll(keys, key => ReadValue(store, key));
            });

        // StringSetAsync(RedisKey, RedisValue, Expiration, ValueCondition, CommandFlags)
        // This is what a three-argument StringSetAsync(key, value, ttl) call resolves to.
        db.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<Expiration>(),
                Arg.Any<ValueCondition>(),
                Arg.Any<CommandFlags>())
            .Returns(callInfo => TryWrite(
                store,
                callInfo.ArgAt<RedisKey>(0),
                callInfo.ArgAt<RedisValue>(1),
                ToWhen(callInfo.ArgAt<ValueCondition>(3))));

        // StringSetAsync(RedisKey, RedisValue, TimeSpan?, When)
        db.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<When>())
            .Returns(callInfo => TryWrite(
                store,
                callInfo.ArgAt<RedisKey>(0),
                callInfo.ArgAt<RedisValue>(1),
                callInfo.ArgAt<When>(3)));

        // StringSetAsync(RedisKey, RedisValue, TimeSpan?, When, CommandFlags)
        db.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<When>(),
                Arg.Any<CommandFlags>())
            .Returns(callInfo => TryWrite(
                store,
                callInfo.ArgAt<RedisKey>(0),
                callInfo.ArgAt<RedisValue>(1),
                callInfo.ArgAt<When>(3)));

        // StringSetAsync(RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags)
        db.StringSetAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<RedisValue>(),
                Arg.Any<TimeSpan?>(),
                Arg.Any<bool>(),
                Arg.Any<When>(),
                Arg.Any<CommandFlags>())
            .Returns(callInfo => TryWrite(
                store,
                callInfo.ArgAt<RedisKey>(0),
                callInfo.ArgAt<RedisValue>(1),
                callInfo.ArgAt<When>(4)));

        // KeyDeleteAsync(RedisKey, CommandFlags)
        db.KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(callInfo => store.TryRemove(callInfo.ArgAt<RedisKey>(0).ToString(), out _));

        // KeyDeleteAsync(RedisKey[], CommandFlags) — returns the number of keys actually removed.
        db.KeyDeleteAsync(Arg.Any<RedisKey[]>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                RedisKey[] keys = callInfo.ArgAt<RedisKey[]>(0);
                long removed = 0;
                foreach (RedisKey key in keys)
                {
                    if (store.TryRemove(key.ToString(), out _))
                    {
                        removed++;
                    }
                }

                return removed;
            });

        // KeyExpireAsync — expiry is not simulated, so this succeeds for any key that exists.
        db.KeyExpireAsync(Arg.Any<RedisKey>(), Arg.Any<TimeSpan?>(), Arg.Any<ExpireWhen>(), Arg.Any<CommandFlags>())
            .Returns(callInfo => store.ContainsKey(callInfo.ArgAt<RedisKey>(0).ToString()));

        // ScriptEvaluateAsync — used by RedisFixedWindowRateLimiter for atomic INCR + EXPIRE.
        // Always returns count 1 so rate limiting never blocks in functional tests.
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create((RedisValue)1L));

        IConnectionMultiplexer redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);
        redis.IsConnected.Returns(true);

        return redis;
    }

    /// <summary>
    /// Reads a key from the backing store, returning <see cref="RedisValue.Null"/> when absent.
    /// </summary>
    private static RedisValue ReadValue(ConcurrentDictionary<string, string> store, RedisKey key)
    {
        if (store.TryGetValue(key.ToString(), out string? value))
        {
            return value;
        }

        return RedisValue.Null;
    }

    /// <summary>
    /// Applies a set subject to an existence condition, mirroring the SETNX / SETXX semantics
    /// callers rely on. Returns false when the condition rejected the write.
    /// </summary>
    private static bool TryWrite(ConcurrentDictionary<string, string> store, RedisKey key, RedisValue value, When when)
    {
        string storeKey = key.ToString();

        if ((when == When.NotExists) && store.ContainsKey(storeKey))
        {
            return false;
        }

        if ((when == When.Exists) && (store.ContainsKey(storeKey) == false))
        {
            return false;
        }

        store[storeKey] = value.ToString();

        return true;
    }

    /// <summary>
    /// Maps the newer <see cref="ValueCondition"/> onto the <see cref="When"/> cases this fake
    /// models. Value and digest comparisons are not supported: no caller uses them, and silently
    /// treating one as unconditional would let a conditional write appear to succeed.
    /// </summary>
    private static When ToWhen(ValueCondition condition)
    {
        if (condition.Equals(ValueCondition.NotExists))
        {
            return When.NotExists;
        }

        if (condition.Equals(ValueCondition.Exists))
        {
            return When.Exists;
        }

        if (condition.Equals(ValueCondition.Always))
        {
            return When.Always;
        }

        throw new NotSupportedException(
            $"FakeRedisConnection does not model the value condition '{condition}'. Add support for it here rather than letting a conditional write silently behave as unconditional.");
    }
}
