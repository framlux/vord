// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using System.Security.Claims;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Users;

/// <summary>
/// Deactivates a user account.
/// </summary>
public sealed class UserDeactivateEndpoint : EndpointWithoutRequest<ApiResponse<object>>
{
    private readonly IUserHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="UserDeactivateEndpoint"/> class.
    /// </summary>
    public UserDeactivateEndpoint(IUserHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/users/{id}/deactivate");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int targetUserId = Route<int>("id");
        string? currentUserIdStr = User.FindFirstValue(ClaimTypes.Actor);
        int currentUserId = int.TryParse(currentUserIdStr, out int uid) ? uid : 0;
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        ServiceResult<object> result = await _handler.DeactivateAsync(targetUserId, currentUserId, tenantId, ct);

        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("User not found"), ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<object>.Error("You cannot deactivate your own account"), ct);

            return;
        }

        await Send.OkAsync(ApiResponse<object>.Ok(new { }, "User deactivated successfully"), cancellation: ct);
    }
}
