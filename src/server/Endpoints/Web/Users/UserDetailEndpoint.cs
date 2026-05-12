// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Models.Users;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Users;

/// <summary>
/// Returns detailed information about a user account.
/// </summary>
public sealed class UserDetailEndpoint : EndpointWithoutRequest<ApiResponse<UserAccountDto>>
{
    private readonly IUserHandler _handler;

    /// <summary>
    /// Creates a new instance of the <see cref="UserDetailEndpoint"/> class.
    /// </summary>
    public UserDetailEndpoint(IUserHandler handler)
    {
        _handler = handler;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/users/{id}");
        Policies("TenantAdmin");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int userId = Route<int>("id");
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);

        ServiceResult<UserAccountDto> result = await _handler.GetDetailAsync(userId, tenantId, ct);

        if (result.IsNotFound)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<UserAccountDto>.Error("User not found"), ct);

            return;
        }

        await Send.OkAsync(ApiResponse<UserAccountDto>.Ok(result.Data!), cancellation: ct);
    }
}
