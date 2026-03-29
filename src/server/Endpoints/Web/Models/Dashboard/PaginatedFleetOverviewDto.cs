// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Dashboard;

/// <summary>
/// Fleet overview response with server-side pagination.
/// </summary>
public sealed class PaginatedFleetOverviewDto
{
    /// <summary>Fleet-wide summary counts (computed across all machines, not just current page).</summary>
    public required FleetSummaryDto Summary { get; set; }

    /// <summary>Paginated machine rows for the current page.</summary>
    public required List<FleetMachineDto> Machines { get; set; }

    /// <summary>Current page number (1-based).</summary>
    public int Page { get; set; }

    /// <summary>Number of items per page.</summary>
    public int PageSize { get; set; }

    /// <summary>Total number of machines matching the current filters.</summary>
    public int TotalCount { get; set; }

    /// <summary>Total number of pages.</summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
}
