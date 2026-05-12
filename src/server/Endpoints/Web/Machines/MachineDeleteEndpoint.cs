// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Soft-deletes a machine.
/// </summary>
public sealed class MachineDeleteEndpoint : EndpointWithoutRequest<ApiResponse<object>>
{
    private readonly IMachineHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineDeleteEndpoint"/> class.
    /// </summary>
    public MachineDeleteEndpoint(IMachineHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Delete("/machines/{id}");
        Policies("MachineAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        long machineId = Route<long>("id");
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (userId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("Unable to identify user"), ct);

            return;
        }

        ServiceResult<ApiResponse<object>> result = await _handler.DeleteAsync(machineId, tenantId, userId.Value, ct);

        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("Machine not found"), ct);

            return;
        }

        await Send.OkAsync(result.Data!, cancellation: ct);
    }
}
