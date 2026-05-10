// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Lists all authorized signing keys for a specific machine, including revoked authorizations.
/// </summary>
public sealed class MachineAuthorizedKeyListEndpoint : EndpointWithoutRequest<ApiResponse<List<MachineAuthorizedKeyDto>>>
{
    private readonly IMachineAuthorizedKeyService _authorizedKeyService;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineAuthorizedKeyListEndpoint"/> class.
    /// </summary>
    public MachineAuthorizedKeyListEndpoint(IMachineAuthorizedKeyService authorizedKeyService)
    {
        _authorizedKeyService = authorizedKeyService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/machines/{machineId}/authorized-keys");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        long machineId = Route<long>("machineId");

        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<List<MachineAuthorizedKeyDto>>.Error("Unable to identify tenant"), ct);

            return;
        }

        ServiceResult<List<MachineAuthorizedKeyDto>> result = await _authorizedKeyService.ListAuthorizedKeysAsync(
            machineId, tenantId.Value, ct);

        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<List<MachineAuthorizedKeyDto>>.Error("Machine not found"), ct);

            return;
        }

        List<MachineAuthorizedKeyDto> data = result.Data ?? [];

        await Send.OkAsync(ApiResponse<List<MachineAuthorizedKeyDto>>.Ok(data), cancellation: ct);
    }
}
