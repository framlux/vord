// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Services.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Auth;

/// <summary>
/// Logs the current user out by clearing the authentication cookie.
/// </summary>
public sealed class AuthLogoutEndpoint : EndpointWithoutRequest<ApiResponse<object>>
{
    private readonly IUserSecurityStampService _securityStampService;
    private readonly ILogger<AuthLogoutEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="AuthLogoutEndpoint"/> class.
    /// </summary>
    /// <param name="securityStampService">The per-user security stamp service.</param>
    /// <param name="logger">The logger instance.</param>
    public AuthLogoutEndpoint(IUserSecurityStampService securityStampService, ILogger<AuthLogoutEndpoint> logger)
    {
        _securityStampService = securityStampService;
        _logger = logger;
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

        await BumpSecurityStampAsync(User, _securityStampService, _logger, ct);

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        await Send.OkAsync(ApiResponse<object>.Ok(new { }), cancellation: ct);
    }

    /// <summary>
    /// Rotates the user's security stamp so the logged-out cookie is immediately invalidated.
    /// When the actor claim cannot be parsed into a user id the bump is skipped and a warning is
    /// logged; logout still proceeds so the caller's cookie is cleared either way.
    /// </summary>
    /// <param name="principal">The authenticated principal being logged out.</param>
    /// <param name="securityStampService">The per-user security stamp service.</param>
    /// <param name="logger">The logger used to record the skipped-bump case.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An awaitable task.</returns>
    internal static async Task BumpSecurityStampAsync(
        ClaimsPrincipal principal,
        IUserSecurityStampService securityStampService,
        ILogger logger,
        CancellationToken ct)
    {
        string? actor = principal.FindFirstValue(ClaimTypes.Actor);
        if (int.TryParse(actor, out int userId))
        {
            await securityStampService.BumpAsync(userId, ct);
        }
        else
        {
            logger.LogWarning("Logout proceeding without a security stamp bump because the user id could not be determined from the actor claim");
        }
    }
}
