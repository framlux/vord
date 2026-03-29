// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Endpoints.Web.Models.Users;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Result containing database-sourced user data for the auth/me endpoint.
/// </summary>
public sealed class AuthMeResult
{
    /// <summary>
    /// The user's internal database ID.
    /// </summary>
    public int UserId { get; init; }

    /// <summary>
    /// Whether the user is a global administrator.
    /// </summary>
    public bool IsGlobalAdmin { get; init; }

    /// <summary>
    /// The tenants and roles assigned to the user.
    /// </summary>
    public List<UserTenantDto> Tenants { get; init; } = new();

    /// <summary>
    /// Whether the user needs to complete onboarding (has no tenant memberships).
    /// </summary>
    public bool NeedsOnboarding { get; init; }
}

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
