// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.Security;

/// <summary>
/// Deletes the Redis-cached role claims for a user so the
/// CookiePrincipalValidator (in the server project) refreshes them from the database on the next request.
/// </summary>
public sealed class RoleCacheInvalidator : IRoleCacheInvalidator
{
    private readonly IConnectionMultiplexer _redis;

    /// <summary>
    /// Initializes a new instance of the <see cref="RoleCacheInvalidator"/> class.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    public RoleCacheInvalidator(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    /// <inheritdoc />
    public async Task InvalidateAsync(int userId, CancellationToken ct)
    {
        IDatabase db = _redis.GetDatabase();
        string cacheKey = $"{IRoleCacheInvalidator.RoleCacheKeyPrefix}{userId}";
        await db.KeyDeleteAsync(cacheKey);
    }
}
