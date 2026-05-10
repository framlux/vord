// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Revokes a signing key authorization for a specific machine.
/// </summary>
public sealed class MachineAuthorizedKeyRevokeEndpoint : EndpointWithoutRequest<ApiResponse<bool>>
{
    private readonly IMachineAuthorizedKeyService _authorizedKeyService;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineAuthorizedKeyRevokeEndpoint"/> class.
    /// </summary>
    public MachineAuthorizedKeyRevokeEndpoint(IMachineAuthorizedKeyService authorizedKeyService)
    {
        _authorizedKeyService = authorizedKeyService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Delete("/machines/{machineId}/authorized-keys/{keyId:int}");
        Policies("MachineAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        long machineId = Route<long>("machineId");
        int keyId = Route<int>("keyId");

        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<bool>.Error("Unable to identify tenant"), ct);

            return;
        }

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (userId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<bool>.Error("Unable to identify user"), ct);

            return;
        }

        ServiceResult<bool> result = await _authorizedKeyService.RevokeAuthorizationAsync(
            machineId, keyId, userId.Value, tenantId.Value, ct);

        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<bool>.Error("Authorization not found"), ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<bool>.Error(result.ErrorMessage ?? "Revocation failed"), ct);

            return;
        }

        await Send.OkAsync(ApiResponse<bool>.Ok(true, "Authorization revoked"), cancellation: ct);
    }
}
