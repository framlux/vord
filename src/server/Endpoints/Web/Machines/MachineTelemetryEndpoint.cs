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
/// Returns paginated telemetry records for a machine.
/// </summary>
public sealed class MachineTelemetryEndpoint : EndpointWithoutRequest<ApiResponse<PaginatedResponse<MachineTelemetryDto>>>
{
    private readonly IMachineDetailHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineTelemetryEndpoint"/> class.
    /// </summary>
    public MachineTelemetryEndpoint(IMachineDetailHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/{id}/telemetry");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        long machineId = Route<long>("id");
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        int page = Query<int?>("page", isRequired: false) ?? 1;
        int pageSize = Query<int?>("pageSize", isRequired: false) ?? 25;
        short? typeFilter = Query<short?>("type", isRequired: false);

        ServiceResult<PaginatedResponse<MachineTelemetryDto>> result =
            await _handler.GetTelemetryAsync(machineId, tenantId, page, pageSize, typeFilter, ct);

        if (result.IsNotFound)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        await Send.OkAsync(ApiResponse<PaginatedResponse<MachineTelemetryDto>>.Ok(result.Data!), cancellation: ct);
    }
}
