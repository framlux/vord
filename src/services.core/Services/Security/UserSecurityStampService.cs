// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Cryptography;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.Security;

/// <summary>
/// Redis-backed per-user security stamp. The stamp has no expiry: it persists until the next
/// bump so a long-lived cookie cannot outlive a revocation event.
/// </summary>
public sealed class UserSecurityStampService : IUserSecurityStampService
{
    private readonly IConnectionMultiplexer _redis;

    /// <summary>
    /// Creates a new instance of the <see cref="UserSecurityStampService"/> class.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    public UserSecurityStampService(IConnectionMultiplexer redis)
    {
        ArgumentNullException.ThrowIfNull(redis);
        _redis = redis;
    }

    /// <inheritdoc/>
    public async Task<string> GetCurrentStampAsync(int userId, CancellationToken ct)
    {
        IDatabase db = _redis.GetDatabase();
        string key = $"{IUserSecurityStampService.StampKeyPrefix}{userId}";
        RedisValue value = await db.StringGetAsync(key);
        if (value.HasValue)
        {
            return value.ToString();
        }

        // No stamp exists yet (first login): mint one so the cookie carries a real value that
        // must match exactly. When.NotExists guards against two concurrent logins racing.
        string newStamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await db.StringSetAsync(key, newStamp, null, false, When.NotExists);
        RedisValue stored = await db.StringGetAsync(key);

        return stored.HasValue ? stored.ToString() : newStamp;
    }

    /// <inheritdoc/>
    public async Task BumpAsync(int userId, CancellationToken ct)
    {
        IDatabase db = _redis.GetDatabase();
        string newStamp = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        await db.StringSetAsync($"{IUserSecurityStampService.StampKeyPrefix}{userId}", newStamp, null, false, When.Always);
    }
}
