// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles retrieval of the current authenticated user's data from the database.
/// </summary>
public interface IAuthMeHandler
{
    /// <summary>
    /// Retrieves the database-sourced user data for the specified external identity.
    /// </summary>
    /// <param name="uniqueId">The user's unique identifier from the identity provider.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the user's database-sourced data.</returns>
    Task<ServiceResult<AuthMeResult>> GetCurrentUserAsync(string uniqueId, CancellationToken ct);
}
