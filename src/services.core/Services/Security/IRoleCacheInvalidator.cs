// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Security;

/// <summary>
/// Invalidates the Redis-cached role claims for a user so the next request
/// picks up the current roles from the database.
/// </summary>
public interface IRoleCacheInvalidator
{
    /// <summary>
    /// Redis key prefix for cached role claims. Shared between
    /// CookiePrincipalValidator (writes) and RoleCacheInvalidator (deletes).
    /// </summary>
    const string RoleCacheKeyPrefix = "user:roles:";

    /// <summary>
    /// Redis key prefix for the cached active/global-admin state. Shared between
    /// CookiePrincipalValidator (reads and writes) and RoleCacheInvalidator (deletes).
    /// </summary>
    const string UserStateCacheKeyPrefix = "user:active:";

    /// <summary>
    /// Deletes the cached role claims for the specified user.
    /// </summary>
    /// <param name="userId">The user whose role cache should be invalidated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Returns an awaitable Task.</returns>
    Task InvalidateAsync(int userId, CancellationToken ct);

    /// <summary>
    /// Deletes the cached active/global-admin state for a user so the next request re-reads it live.
    /// </summary>
    /// <param name="userId">The user whose state cache should be invalidated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Returns an awaitable Task.</returns>
    Task InvalidateUserStateAsync(int userId, CancellationToken ct);
}
