// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Handles cleanup of tier-gated resources when a tenant is downgraded.
/// </summary>
/// <remarks>
/// Both cleanups write on the caller's connection, so they belong inside the transaction that changes
/// the tenant's tier, before its commit. The tier write holds the subscription row's lock until then,
/// which makes an enterprise agreement being applied to the same tenant wait for the cleanup instead
/// of landing between a committed tier change and a cleanup that would then run on an Enterprise
/// tenant. What cannot be undone by a rollback, evicting cached API keys, is left to
/// <see cref="EvictApiKeysAsync"/> after the commit.
/// </remarks>
public interface IDowngradeCleanupService
{
    /// <summary>
    /// Cleans up resources that require Team tier when downgrading to Pro.
    /// Disables custom OIDC configuration and custom alert rules.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CleanupForProTierAsync(int tenantId, CancellationToken ct);

    /// <summary>
    /// Cleans up resources that require a paid tier when downgrading to Free.
    /// Disables all alert rules, OIDC configuration, and webhook endpoints, and soft-deletes the
    /// machines beyond the Free limit.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The API-key hashes of the machines it soft-deleted, to be handed to <see cref="EvictApiKeysAsync"/>
    /// once the caller's transaction has committed.
    /// </returns>
    Task<IReadOnlyList<string>> CleanupForFreeTierAsync(int tenantId, CancellationToken ct);

    /// <summary>
    /// Removes the cached authentication results of API keys whose machines were soft-deleted, so they
    /// stop authenticating now rather than when the cache entry expires. Call only after the
    /// transaction that deleted the machines has committed: evicting earlier lets a request that still
    /// sees the machine as active put it straight back in the cache. Best-effort; the cache TTL is the
    /// backstop.
    /// </summary>
    /// <param name="apiKeyHashes">The hashes returned by <see cref="CleanupForFreeTierAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task EvictApiKeysAsync(IReadOnlyList<string> apiKeyHashes, CancellationToken ct);
}
