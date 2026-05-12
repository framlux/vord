// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Models.Dashboard;
using Framlux.FleetManagement.Services.Core.Models.Machines;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Service for reading MachineStateSummary and mapping to fleet/detail DTOs.
/// </summary>
public interface IMachineStateService
{
    /// <summary>
    /// Returns the fleet overview with summary statistics computed across all machines
    /// and a paginated subset of machine rows.
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of machines per page.</param>
    /// <param name="tenantId">The tenant ID of the requesting user.</param>
    /// <param name="search">Optional search filter for name, hostname, or IP.</param>
    /// <param name="statusFilter">Optional health status filter (healthy, warning, critical, offline).</param>
    /// <param name="sortBy">Sort field (name, status, cpu, memory, disk).</param>
    /// <param name="sortDir">Sort direction (asc, desc).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PaginatedFleetOverviewDto> GetFleetOverviewAsync(
        int page,
        int pageSize,
        int? tenantId,
        string? search,
        string? statusFilter,
        string sortBy,
        string sortDir,
        CancellationToken ct);

    /// <summary>
    /// Returns the full detail view for a single machine.
    /// </summary>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="tenantId">The tenant ID of the requesting user.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<MachineDetailDto?> GetMachineDetailAsync(long machineId, int? tenantId, CancellationToken ct);
}
