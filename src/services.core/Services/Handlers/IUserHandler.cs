// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Users;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles user management operations.
/// </summary>
public interface IUserHandler
{
    /// <summary>
    /// Lists users in a tenant.
    /// </summary>
    /// <param name="tenantId">The tenant ID to scope the query.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the list of user DTOs.</returns>
    Task<ServiceResult<List<UserAccountDto>>> ListAsync(int? tenantId, CancellationToken ct);

    /// <summary>
    /// Gets detail for a user in a tenant.
    /// </summary>
    /// <param name="userId">The target user ID.</param>
    /// <param name="tenantId">The tenant ID to scope the query.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the user DTO.</returns>
    Task<ServiceResult<UserAccountDto>> GetDetailAsync(int userId, int? tenantId, CancellationToken ct);

    /// <summary>
    /// Deactivates a user from a tenant.
    /// </summary>
    /// <param name="targetUserId">The user to deactivate.</param>
    /// <param name="currentUserId">The user performing the action.</param>
    /// <param name="tenantId">The tenant ID to scope the deactivation.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result.</returns>
    Task<ServiceResult<object>> DeactivateAsync(int targetUserId, int currentUserId, int? tenantId, CancellationToken ct);
}
