// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Framlux.FleetManagement.Services.Core.Security;

/// <summary>
/// Deletes the Redis-cached API key auth result so the ApiKeyAuthenticationHandler (in the server
/// project) re-resolves the key against the database on the next request. Used by services that
/// revoke or replace a key — soft-delete and reissue — which know the stored hash but not the
/// plaintext.
/// </summary>
public sealed class ApiKeyCacheInvalidator : IApiKeyCacheInvalidator
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<ApiKeyCacheInvalidator> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiKeyCacheInvalidator"/> class.
    /// </summary>
    /// <param name="redis">The Redis connection multiplexer.</param>
    /// <param name="logger">The logger.</param>
    public ApiKeyCacheInvalidator(IConnectionMultiplexer redis, ILogger<ApiKeyCacheInvalidator> logger)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(logger);
        _redis = redis;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task InvalidateByHashAsync(string keyHash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyHash);
        try
        {
            IDatabase db = _redis.GetDatabase();
            await db.KeyDeleteAsync($"{IApiKeyCacheInvalidator.CacheKeyPrefix}{keyHash}");
        }
        catch (RedisException ex)
        {
            // A failed cache delete must not abort the surrounding operation; the entry expires on
            // its own TTL. The handler logs Redis faults on the read/write path; here the caller's
            // operation (delete/reissue) has already committed and should still report success.
            _logger.LogWarning(ex, "Redis cache invalidation failed for API key hash; entry will expire on TTL");
        }
    }
}
