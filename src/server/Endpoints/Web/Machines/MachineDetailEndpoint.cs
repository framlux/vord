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
/// Returns detailed information about a specific machine.
/// </summary>
public sealed class MachineDetailEndpoint : EndpointWithoutRequest<ApiResponse<MachineDto>>
{
    private readonly IMachineDetailHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineDetailEndpoint"/> class.
    /// </summary>
    public MachineDetailEndpoint(IMachineDetailHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/{id}");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        long machineId = Route<long>("id");
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        ServiceResult<MachineDto> result = await _handler.GetDetailAsync(machineId, tenantId, ct);

        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<MachineDto>.Error("Machine not found"), ct);

            return;
        }

        await Send.OkAsync(ApiResponse<MachineDto>.Ok(result.Data!), cancellation: ct);
    }
}
