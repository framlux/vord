// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Services.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Auth;

/// <summary>
/// Logs the current user out by clearing the authentication cookie.
/// </summary>
public sealed class AuthLogoutEndpoint : EndpointWithoutRequest<ApiResponse<object>>
{
    private readonly IUserSecurityStampService _securityStampService;

    /// <summary>
    /// Creates a new instance of the <see cref="AuthLogoutEndpoint"/> class.
    /// </summary>
    /// <param name="securityStampService">The per-user security stamp service.</param>
    public AuthLogoutEndpoint(IUserSecurityStampService securityStampService)
    {
        _securityStampService = securityStampService;
    }

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

        string? actor = User.FindFirstValue(ClaimTypes.Actor);
        if (int.TryParse(actor, out int userId))
        {
            await _securityStampService.BumpAsync(userId, ct);
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        await Send.OkAsync(ApiResponse<object>.Ok(new { }), cancellation: ct);
    }
}
