// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Service for searching machines with advanced filter criteria.
/// </summary>
public interface IMachineSearchService
{
    /// <summary>
    /// Searches machines using the provided criteria and returns a paginated result.
    /// </summary>
    /// <param name="criteria">The search criteria containing filters, pagination, and sort options.</param>
    /// <param name="tenantId">The tenant ID of the requesting user.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PaginatedResponse<FleetMachineDto>> SearchAsync(
        MachineSearchCriteria criteria,
        int? tenantId,
        CancellationToken ct);
}
