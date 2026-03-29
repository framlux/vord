// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Returns a paginated list of approved machines.
/// </summary>
public sealed class MachineListEndpoint : EndpointWithoutRequest<ApiResponse<PaginatedResponse<MachineDto>>>
{
    private readonly IMachineHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineListEndpoint"/> class.
    /// </summary>
    public MachineListEndpoint(IMachineHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int page = Math.Max(1, Query<int?>("page", isRequired: false) ?? 1);
        int pageSize = Math.Clamp(Query<int?>("pageSize", isRequired: false) ?? 25, 1, 100);
        string? search = Query<string?>("search", isRequired: false);
        string? osFilter = Query<string?>("os", isRequired: false);
        string? typeFilter = Query<string?>("type", isRequired: false);
        string? statusFilter = Query<string?>("status", isRequired: false);
        string sortBy = Query<string?>("sortBy", isRequired: false) ?? "name";
        string sortDir = Query<string?>("sortDir", isRequired: false) ?? "asc";
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        ServiceResult<PaginatedResponse<MachineDto>> result = await _handler.ListAsync(
            page, pageSize, tenantId, search, osFilter, typeFilter, statusFilter, sortBy, sortDir, ct);

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;

            return;
        }

        await Send.OkAsync(ApiResponse<PaginatedResponse<MachineDto>>.Ok(result.Data!), cancellation: ct);
    }
}
