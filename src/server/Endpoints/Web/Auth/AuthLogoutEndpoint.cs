// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Auth;

/// <summary>
/// Logs the current user out by clearing the authentication cookie.
/// </summary>
public sealed class AuthLogoutEndpoint : EndpointWithoutRequest<ApiResponse<object>>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/logout");
        Version(1);
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        if (User?.Identity?.IsAuthenticated != true)
        {
            await Send.OkAsync(ApiResponse<object>.Ok(new { }), cancellation: ct);

            return;
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        await Send.OkAsync(ApiResponse<object>.Ok(new { }), cancellation: ct);
    }
}
