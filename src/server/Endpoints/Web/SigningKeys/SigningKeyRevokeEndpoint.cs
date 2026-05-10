// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;

namespace Framlux.FleetManagement.Server.Endpoints.Web.SigningKeys;

/// <summary>
/// Revokes a signing key by ID.
/// </summary>
public sealed class SigningKeyRevokeEndpoint : EndpointWithoutRequest<ApiResponse<bool>>
{
    private readonly ISigningKeyService _signingKeyService;

    /// <summary>
    /// Creates a new instance of the <see cref="SigningKeyRevokeEndpoint"/> class.
    /// </summary>
    public SigningKeyRevokeEndpoint(ISigningKeyService signingKeyService)
    {
        _signingKeyService = signingKeyService;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Delete("/signing-keys/{id}");
        Policies("MachineAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int keyId = Route<int>("id");

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

        // Check if the user is a TenantAdmin or GlobalAdmin for permission to revoke others' keys.
        bool isAdmin = User.HasClaim("iga", true.ToString());
        bool isTenantAdmin = User.FindAll(ClaimTypes.Role)
            .Any(c => c.Value.EndsWith(":1")); // :1 = TenantAdmin role

        ServiceResult<bool> result = await _signingKeyService.RevokeKeyAsync(
            keyId, userId.Value, tenantId.Value, isAdmin || isTenantAdmin, ct);

        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<bool>.Error("Signing key not found"), ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<bool>.Error("Cannot revoke this key"), ct);

            return;
        }

        await Send.OkAsync(ApiResponse<bool>.Ok(true, "Key revoked"), cancellation: ct);
    }
}
