// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Machines;

/// <summary>
/// Revokes a registration token.
/// </summary>
public sealed class RevokeRegistrationTokenEndpoint : EndpointWithoutRequest<ApiResponse<object>>
{
    private readonly IRegistrationTokenHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="RevokeRegistrationTokenEndpoint"/> class.
    /// </summary>
    public RevokeRegistrationTokenEndpoint(IRegistrationTokenHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Delete("/machines/registration-tokens/{id}");
        Policies("MachineAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        long tokenId = Route<long>("id");
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("Unable to identify tenant"), ct);

            return;
        }

        int? userId = TenantClaimHelper.GetUserIdFromClaims(User);
        if (userId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("Unable to identify user"), ct);

            return;
        }

        ServiceResult<object> result = await _handler.RevokeAsync(tokenId, tenantId.Value, userId.Value, ct);

        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("Registration token not found"), ct);

            return;
        }

        await Send.OkAsync(ApiResponse<object>.Ok(new { }, "Token revoked"), cancellation: ct);
    }
}
