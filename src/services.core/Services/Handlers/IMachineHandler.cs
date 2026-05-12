// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Models;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles machine management operations.
/// </summary>
public interface IMachineHandler
{
    /// <summary>
    /// Soft-deletes a machine.
    /// </summary>
    /// <param name="machineId">The machine ID to delete.</param>
    /// <param name="tenantId">The tenant ID of the requesting user.</param>
    /// <param name="userId">The user performing the deletion.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the API response.</returns>
    Task<ServiceResult<ApiResponse<object>>> DeleteAsync(long machineId, int? tenantId, int userId, CancellationToken ct);

    /// <summary>
    /// Updates a machine's editable metadata (name, description, location).
    /// </summary>
    /// <param name="machineId">The machine ID to update.</param>
    /// <param name="tenantId">The tenant ID of the requesting user.</param>
    /// <param name="userId">The user performing the update.</param>
    /// <param name="name">The new machine display name.</param>
    /// <param name="description">The new description (null to clear).</param>
    /// <param name="location">The new location (null to clear).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the updated machine DTO.</returns>
    Task<ServiceResult<ApiResponse<MachineDto>>> UpdateAsync(
        long machineId,
        int? tenantId,
        int userId,
        string name,
        string? description,
        string? location,
        CancellationToken ct);

    /// <summary>
    /// Returns a paginated, filtered, and sorted list of machines.
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="tenantId">The tenant ID of the requesting user.</param>
    /// <param name="search">Optional search term for hostname/name.</param>
    /// <param name="osFilter">Optional OS filter.</param>
    /// <param name="typeFilter">Optional machine type filter.</param>
    /// <param name="statusFilter">Optional online/offline status filter.</param>
    /// <param name="sortBy">The field to sort by.</param>
    /// <param name="sortDir">The sort direction (asc/desc).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the paginated machine list.</returns>
    Task<ServiceResult<PaginatedResponse<MachineDto>>> ListAsync(
        int page,
        int pageSize,
        int? tenantId,
        string? search,
        string? osFilter,
        string? typeFilter,
        string? statusFilter,
        string sortBy,
        string sortDir,
        CancellationToken ct);
}
