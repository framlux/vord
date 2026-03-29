// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// Handle representing an acquired distributed lock. Disposing releases the lock.
/// </summary>
public sealed class LockHandle : IAsyncDisposable
{
    private readonly IDatabase _db;
    private readonly string _key;
    private readonly string _value;

    /// <summary>
    /// Creates a new lock handle.
    /// </summary>
    /// <param name="db">The Redis database instance.</param>
    /// <param name="key">The lock key.</param>
    /// <param name="value">The lock owner value for safe release.</param>
    public LockHandle(IDatabase db, string key, string value)
    {
        _db = db;
        _key = key;
        _value = value;
    }

    /// <summary>
    /// Releases the lock if it is still owned by this instance.
    /// Uses a Lua script for atomic check-and-delete.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        const string luaScript = """
            if redis.call("get", KEYS[1]) == ARGV[1] then
                return redis.call("del", KEYS[1])
            else
                return 0
            end
            """;

        await _db.ScriptEvaluateAsync(luaScript, [(RedisKey)_key], [(RedisValue)_value]);
    }
}
