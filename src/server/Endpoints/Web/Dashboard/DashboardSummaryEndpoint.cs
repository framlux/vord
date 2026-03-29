// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Dashboard;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Dashboard;

/// <summary>
/// Returns dashboard summary statistics.
/// </summary>
public sealed class DashboardSummaryEndpoint : EndpointWithoutRequest<ApiResponse<DashboardSummaryDto>>
{
    private readonly IDashboardHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="DashboardSummaryEndpoint"/> class.
    /// </summary>
    public DashboardSummaryEndpoint(IDashboardHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/dashboard/summary");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        ServiceResult<DashboardSummaryDto> result = await _handler.GetSummaryAsync(tenantId, ct);

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;

            return;
        }

        await Send.OkAsync(ApiResponse<DashboardSummaryDto>.Ok(result.Data!), cancellation: ct);
    }
}
