// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Invitations;

/// <summary>
/// Response from accepting an invitation.
/// </summary>
public sealed class InvitationAcceptResponse
{
    /// <summary>
    /// The tenant ID the user was added to.
    /// </summary>
    public int TenantId { get; set; }
}

/// <summary>
/// Accepts a tenant invitation.
/// </summary>
public sealed class InvitationAcceptEndpoint : EndpointWithoutRequest<ApiResponse<InvitationAcceptResponse>>
{
    private readonly IInvitationHandler _handler;
    private readonly AuthCookieOptions _authCookieOptions;
    private readonly ILogger<InvitationAcceptEndpoint> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="InvitationAcceptEndpoint"/> class.
    /// </summary>
    public InvitationAcceptEndpoint(
        IInvitationHandler handler,
        IOptions<AuthCookieOptions> authCookieOptions,
        ILogger<InvitationAcceptEndpoint> logger)
    {
        _handler = handler;
        _authCookieOptions = authCookieOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/invitations/{token}/accept");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        string token = Route<string>("token") ?? string.Empty;
        string userEmail = User.FindFirstValue(ClaimTypes.Email)
            ?? User.FindFirstValue("email")
            ?? string.Empty;
        string? userIdStr = User.FindFirstValue(ClaimTypes.Actor);
        int userId = (int.TryParse(userIdStr, out int uid)) ? uid : 0;
        string uniqueId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? string.Empty;

        ServiceResult<InvitationAcceptResult> result = await _handler.AcceptAsync(token, userEmail, userId, uniqueId, ct);

        if (result.IsNotFound)
        {
            await Send.NotFoundAsync(ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<InvitationAcceptResponse>.Error(result.Data?.ErrorMessage ?? "Unknown error"), ct);

            return;
        }

        // Re-issue auth cookie with updated claims (HTTP-specific, stays in endpoint)
        ClaimsIdentity identity = (ClaimsIdentity)User.Identity!;
        bool refreshed = await SocialAuthEvents.PopulateUserClaimsAsync(identity, HttpContext, ct);
        if (refreshed)
        {
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity));
        }

        // Set vord_tenant cookie to the invitation's tenant
        string cookieDomain = _authCookieOptions.CookieDomain;
        HttpContext.Response.Cookies.Append("vord_tenant", result.Data!.TenantId.ToString(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = true,
            Path = "/",
            MaxAge = TimeSpan.FromDays(31),
            Domain = string.IsNullOrEmpty(cookieDomain) == false ? cookieDomain : null,
        });

        _logger.LogInformation("Invitation accepted: user {UserId} joined tenant {TenantId}", userId, result.Data!.TenantId);

        await Send.OkAsync(ApiResponse<InvitationAcceptResponse>.Ok(new InvitationAcceptResponse
        {
            TenantId = result.Data!.TenantId,
        }), cancellation: ct);
    }
}
