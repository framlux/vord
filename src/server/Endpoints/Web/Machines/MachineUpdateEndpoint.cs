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
/// Updates a machine's editable metadata (name, description, location).
/// </summary>
public sealed class MachineUpdateEndpoint : Endpoint<UpdateMachineRequest, ApiResponse<MachineDto>>
{
    private readonly IMachineHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineUpdateEndpoint"/> class.
    /// </summary>
    public MachineUpdateEndpoint(IMachineHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Verbs(Http.PATCH);
        Routes("/machines/{id}");
        Policies("MachineAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(UpdateMachineRequest req, CancellationToken ct)
    {
        long machineId = Route<long>("id");
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (userId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<MachineDto>.Error("Unable to identify user"), ct);

            return;
        }

        ServiceResult<ApiResponse<MachineDto>> result = await _handler.UpdateAsync(
            machineId, tenantId, userId.Value, req.Name, req.Description, req.Location, ct);

        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<MachineDto>.Error("Machine not found"), ct);

            return;
        }

        if (result.StatusCode == 400)
        {
            HttpContext.Response.StatusCode = 400;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<MachineDto>.Error(result.ErrorMessage ?? "Invalid request"), ct);

            return;
        }

        await Send.OkAsync(result.Data!, cancellation: ct);
    }
}
