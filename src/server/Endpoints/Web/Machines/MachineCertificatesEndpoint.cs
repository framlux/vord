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
/// Returns certificates issued to a machine (paginated).
/// </summary>
public sealed class MachineCertificatesEndpoint : EndpointWithoutRequest<ApiResponse<PaginatedResponse<MachineCertificateDto>>>
{
    private readonly IMachineDetailHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineCertificatesEndpoint"/> class.
    /// </summary>
    public MachineCertificatesEndpoint(IMachineDetailHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/{id}/certificates");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        long machineId = Route<long>("id");
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        int page = Math.Max(1, Query<int?>("page", isRequired: false) ?? 1);
        int pageSize = Math.Clamp(Query<int?>("pageSize", isRequired: false) ?? 25, 1, 100);

        ServiceResult<PaginatedResponse<MachineCertificateDto>> result =
            await _handler.GetCertificatesAsync(machineId, tenantId, page, pageSize, ct);

        if (result.IsNotFound)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        await Send.OkAsync(ApiResponse<PaginatedResponse<MachineCertificateDto>>.Ok(result.Data!), cancellation: ct);
    }
}
