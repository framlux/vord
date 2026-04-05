// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Dashboard;
using Framlux.FleetManagement.Server.Services.Machines;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Dashboard;

/// <summary>
/// Returns the fleet overview with summary and paginated per-machine state from the MachineStateSummary cache.
/// </summary>
public sealed class DashboardFleetEndpoint : EndpointWithoutRequest<ApiResponse<PaginatedFleetOverviewDto>>
{
    private readonly IMachineStateService _stateService;

    /// <summary>
    /// Creates a new instance of the <see cref="DashboardFleetEndpoint"/> class.
    /// </summary>
    public DashboardFleetEndpoint(IMachineStateService stateService)
    {
        _stateService = stateService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/dashboard/fleet");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int page = Math.Max(1, Query<int?>("page", isRequired: false) ?? 1);
        int pageSize = Math.Clamp(Query<int?>("pageSize", isRequired: false) ?? 25, 1, 100);
        string? search = Query<string?>("search", isRequired: false);
        string? statusFilter = Query<string?>("status", isRequired: false);
        string sortBy = Query<string?>("sortBy", isRequired: false) ?? "name";
        string sortDir = Query<string?>("sortDir", isRequired: false) ?? "asc";
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        PaginatedFleetOverviewDto overview = await _stateService.GetFleetOverviewAsync(
            page, pageSize, tenantId, search, statusFilter, sortBy, sortDir, ct);

        await Send.OkAsync(ApiResponse<PaginatedFleetOverviewDto>.Ok(overview), cancellation: ct);
    }
}
