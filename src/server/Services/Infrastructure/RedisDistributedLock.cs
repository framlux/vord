// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using StackExchange.Redis;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// Redis-backed distributed lock implementation using SET NX EX.
/// </summary>
public sealed class RedisDistributedLock : IDistributedLock
{
    private readonly IConnectionMultiplexer _redis;
    private readonly string _instanceId = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Creates a new instance of the <see cref="RedisDistributedLock"/> class.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    public RedisDistributedLock(IConnectionMultiplexer redis)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
    }

    /// <inheritdoc/>
    public async Task<LockHandle?> TryAcquireAsync(string lockKey, TimeSpan ttl)
    {
        IDatabase db = _redis.GetDatabase();
        bool acquired = await db.StringSetAsync(lockKey, _instanceId, ttl, When.NotExists);
        if (acquired)
        {
            return new LockHandle(db, lockKey, _instanceId);
        }

        return null;
    }
}
