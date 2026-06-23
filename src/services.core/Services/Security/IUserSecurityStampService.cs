// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Security;

/// <summary>
/// Reads and bumps a per-user security stamp. The stamp is minted into the auth cookie at login
/// and re-checked on every request; bumping it server-side immediately invalidates every cookie
/// that carried the previous value (logout, deactivation, role/admin changes).
/// </summary>
public interface IUserSecurityStampService
{
    /// <summary>Redis key prefix for the per-user stamp. Full key is "{Prefix}{userId}".</summary>
    const string StampKeyPrefix = "user:stamp:";

    /// <summary>
    /// Returns the user's current stamp, minting a fresh random value if none exists yet so that
    /// every issued cookie carries a real, non-empty stamp that must match exactly on each request.
    /// </summary>
    /// <param name="userId">The user whose stamp to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current stamp value.</returns>
    Task<string> GetCurrentStampAsync(int userId, CancellationToken ct);

    /// <summary>
    /// Rotates the user's stamp to a new random value, invalidating all existing cookies.
    /// </summary>
    /// <param name="userId">The user whose stamp to rotate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An awaitable task.</returns>
    Task BumpAsync(int userId, CancellationToken ct);
}
