// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Security;

/// <summary>
/// Invalidates the Redis-cached role claims for a user so the next request
/// picks up the current roles from the database.
/// </summary>
public interface IRoleCacheInvalidator
{
    /// <summary>
    /// Deletes the cached role claims for the specified user.
    /// </summary>
    /// <param name="userId">The user whose role cache should be invalidated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Returns an awaitable Task.</returns>
    Task InvalidateAsync(int userId, CancellationToken ct);
}
