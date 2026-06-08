// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Defines operations for managing tenant members.
/// </summary>
public interface IMemberHandler
{
    /// <summary>
    /// Removes a member from the specified tenant.
    /// </summary>
    /// <param name="targetUserId">The ID of the user to remove.</param>
    /// <param name="tenantId">The tenant ID, or null if not available.</param>
    /// <param name="currentUserId">The ID of the user performing the removal.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the API response.</returns>
    Task<ServiceResult<ApiResponse<object>>> RemoveAsync(int targetUserId, int? tenantId, int currentUserId, CancellationToken ct);

    /// <summary>
    /// Changes the role of a member in the specified tenant.
    /// </summary>
    /// <param name="targetUserId">The ID of the user whose role is being changed.</param>
    /// <param name="tenantId">The tenant ID, or null if not available.</param>
    /// <param name="currentUserId">The ID of the user performing the role change.</param>
    /// <param name="newRole">The new role to assign as a string.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the API response.</returns>
    Task<ServiceResult<ApiResponse<object>>> ChangeRoleAsync(int targetUserId, int? tenantId, int currentUserId, string newRole, CancellationToken ct);
}
