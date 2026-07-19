// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Lists all authorized signing keys for a specific machine, including revoked authorizations.
/// </summary>
public sealed class MachineAuthorizedKeyListEndpoint : EndpointWithoutRequest<ApiResponse<List<MachineAuthorizedKeyDto>>>
{
    private readonly IMachineAuthorizedKeyService _authorizedKeyService;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineAuthorizedKeyListEndpoint"/> class.
    /// </summary>
    public MachineAuthorizedKeyListEndpoint(IMachineAuthorizedKeyService authorizedKeyService, ITenantContext tenantContext)
    {
        _authorizedKeyService = authorizedKeyService;
        _tenantContext = tenantContext;
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

        int? tenantId = _tenantContext.TenantId;
        if (tenantId is null)
        {
            await HttpContext.SendApiErrorAsync(401, "Unable to identify tenant", ct);

            return;
        }

        ServiceResult<List<MachineAuthorizedKeyDto>> result = await _authorizedKeyService.ListAuthorizedKeysAsync(
            machineId, tenantId.Value, ct);

        if (result.IsNotFound)
        {
            await HttpContext.SendApiErrorAsync(404, "Machine not found", ct);

            return;
        }

        List<MachineAuthorizedKeyDto> data = result.Data ?? [];

        await Send.OkAsync(ApiResponse<List<MachineAuthorizedKeyDto>>.Ok(data), cancellation: ct);
    }
}
