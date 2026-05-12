// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Request for the machine detail full endpoint.
/// </summary>
public sealed class MachineDetailFullRequest
{
    /// <summary>Machine ID.</summary>
    public long Id { get; set; }
}

/// <summary>
/// Returns the full detail view for a single machine including all telemetry sections.
/// </summary>
public sealed class MachineDetailFullEndpoint : Endpoint<MachineDetailFullRequest, ApiResponse<MachineDetailDto>>
{
    private readonly IMachineDetailHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineDetailFullEndpoint"/> class.
    /// </summary>
    public MachineDetailFullEndpoint(IMachineDetailHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/{Id}/detail");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(MachineDetailFullRequest req, CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        ServiceResult<MachineDetailDto> result = await _handler.GetFullDetailAsync(req.Id, tenantId, ct);

        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<MachineDetailDto>.Error("Machine not found"), ct);

            return;
        }

        await Send.OkAsync(ApiResponse<MachineDetailDto>.Ok(result.Data!), cancellation: ct);
    }
}
