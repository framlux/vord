// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

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
        string? userIdStr = User.FindFirstValue(ClaimTypes.Actor);
        int userId = int.TryParse(userIdStr, out int uid) ? uid : 0;
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        ServiceResult<ApiResponse<object>> result = await _handler.DeleteAsync(machineId, tenantId, userId, ct);

        if (result.IsNotFound)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        await Send.OkAsync(result.Data!, cancellation: ct);
    }
}
