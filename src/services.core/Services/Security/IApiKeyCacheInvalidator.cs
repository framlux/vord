// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Security;

/// <summary>
/// Invalidates the Redis-cached API key auth result for a machine so a revoked or reissued key
/// stops authenticating immediately rather than lingering until its cache TTL expires.
/// </summary>
public interface IApiKeyCacheInvalidator
{
    /// <summary>
    /// Redis key prefix for cached API key auth results. Shared between the in-pipeline
    /// ApiKeyAuthenticationHandler (writes) and this out-of-pipeline invalidator (deletes) so the
    /// prefix never drifts between the two.
    /// </summary>
    const string CacheKeyPrefix = "apikey:";

    /// <summary>
    /// Deletes the cached auth result keyed by the supplied API key hash. The cache entry is keyed
    /// by the lowercase SHA-256 hex of the plaintext key, which is exactly the value stored as
    /// <c>Machine.ApiKeyHash</c>, so callers invalidate without ever handling the plaintext.
    /// </summary>
    /// <param name="keyHash">The lowercase SHA-256 hex hash of the API key, as stored on the machine.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Returns an awaitable Task.</returns>
    Task InvalidateByHashAsync(string keyHash, CancellationToken ct);
}
