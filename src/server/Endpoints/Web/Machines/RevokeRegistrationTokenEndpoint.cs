// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
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
            await Send.NotFoundAsync(ct);

            return;
        }

        string? userIdStr = User.FindFirstValue(ClaimTypes.Actor);
        int userId = int.TryParse(userIdStr, out int uid) ? uid : 0;

        ServiceResult<object> result = await _handler.RevokeAsync(tokenId, tenantId.Value, userId, ct);

        if (result.IsNotFound)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        await Send.OkAsync(ApiResponse<object>.Ok(new { }, "Token revoked"), cancellation: ct);
    }
}
