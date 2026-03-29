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
/// Returns the latest telemetry record per type for a machine.
/// </summary>
public sealed class MachineTelemetryLatestEndpoint : EndpointWithoutRequest<ApiResponse<List<MachineTelemetryDto>>>
{
    private readonly IMachineDetailHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineTelemetryLatestEndpoint"/> class.
    /// </summary>
    public MachineTelemetryLatestEndpoint(IMachineDetailHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/{id}/telemetry/latest");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        long machineId = Route<long>("id");
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        ServiceResult<List<MachineTelemetryDto>> result = await _handler.GetLatestTelemetryAsync(machineId, tenantId, ct);

        if (result.IsNotFound)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        await Send.OkAsync(ApiResponse<List<MachineTelemetryDto>>.Ok(result.Data!), cancellation: ct);
    }
}
