// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Models.Dashboard;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles dashboard summary data retrieval.
/// </summary>
public interface IDashboardHandler
{
    /// <summary>
    /// Gets the dashboard summary statistics.
    /// </summary>
    /// <param name="tenantId">The tenant ID of the requesting user.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the dashboard summary data.</returns>
    Task<ServiceResult<DashboardSummaryDto>> GetSummaryAsync(int? tenantId, CancellationToken ct);
}
