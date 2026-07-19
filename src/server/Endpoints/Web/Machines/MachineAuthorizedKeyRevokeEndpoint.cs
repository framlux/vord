// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Revokes a signing key authorization for a specific machine.
/// </summary>
public sealed class MachineAuthorizedKeyRevokeEndpoint : EndpointWithoutRequest<ApiResponse<bool>>
{
    private readonly IMachineAuthorizedKeyService _authorizedKeyService;
    private readonly ITenantContext _tenantContext;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineAuthorizedKeyRevokeEndpoint"/> class.
    /// </summary>
    public MachineAuthorizedKeyRevokeEndpoint(IMachineAuthorizedKeyService authorizedKeyService, ITenantContext tenantContext)
    {
        _authorizedKeyService = authorizedKeyService;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Delete("/machines/{machineId}/authorized-keys/{keyId:int}");
        Policies("MachineAdmin");
        Tags(EndpointTags.RequiresTenant);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        long machineId = Route<long>("machineId");
        int keyId = Route<int>("keyId");

        int tenantId = _tenantContext.RequireTenantId();

        int? userId = _tenantContext.UserId;
        if (userId is null)
        {
            await HttpContext.SendApiErrorAsync(401, "Unable to identify user", ct);

            return;
        }

        ServiceResult<bool> result = await _authorizedKeyService.RevokeAuthorizationAsync(
            machineId, keyId, userId.Value, tenantId, ct);

        if (result.IsNotFound)
        {
            await HttpContext.SendApiErrorAsync(404, "Authorization not found", ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            await HttpContext.SendApiErrorAsync(result.StatusCode, result.ErrorMessage ?? "Revocation failed", ct);

            return;
        }

        await Send.OkAsync(ApiResponse<bool>.Ok(true, "Authorization revoked"), cancellation: ct);
    }
}
