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
/// Returns detailed information about a specific machine.
/// </summary>
public sealed class MachineDetailEndpoint : EndpointWithoutRequest<ApiResponse<MachineDto>>
{
    private readonly MachineDetailHandler _handler;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineDetailEndpoint"/> class.
    /// </summary>
    public MachineDetailEndpoint(MachineDetailHandler handler, ITenantContext tenantContext)
    {
        _handler = handler;
        _tenantContext = tenantContext;
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
        int? tenantId = _tenantContext.TenantId;

        ServiceResult<MachineDto> result = await _handler.GetDetailAsync(machineId, tenantId, ct);

        if (result.IsNotFound)
        {
            await HttpContext.SendApiErrorAsync(404, "Machine not found", ct);

            return;
        }

        await Send.OkAsync(ApiResponse<MachineDto>.Ok(result.Data!), cancellation: ct);
    }
}
