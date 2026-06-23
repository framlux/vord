// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Options;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
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
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<InvitationAcceptResponse>.Error("Invitation not found or expired"), ct);

            return;
        }

        if (result.IsSuccess == false)
        {
            HttpContext.Response.StatusCode = result.StatusCode;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<InvitationAcceptResponse>.Error(result.Data?.ErrorMessage ?? "Unknown error"), ct);

            return;
        }

        // Re-issue auth cookie with updated claims (HTTP-specific, stays in endpoint).
        // The provider claim was minted alongside the principal at login. Identity resolution is
        // provider-scoped, so the refresh must look the user up under the SAME provider they are
        // already signed in with — otherwise the lookup misses the real account and may create a
        // duplicate Unknown-provider account. Absent, unparseable, or out-of-range values fall
        // back to Unknown, matching how AuthMeEndpoint resolves the provider.
        AuthProviderType provider = (Enum.TryParse(User.FindFirstValue(SecurityStampClaims.AuthProviderClaim), out AuthProviderType parsedProvider) && Enum.IsDefined(parsedProvider))
            ? parsedProvider
            : AuthProviderType.Unknown;

        // CustomOidc subjects are namespaced as "tenant:{id}:{sub}" using the tenant id stashed in
        // HttpContext.Items at challenge time. That item is never present on an invitation-accept
        // request, so PopulateUserClaimsAsync cannot re-derive the namespaced subject for CustomOidc
        // and would throw while trying. The user remains correctly signed in with their existing
        // cookie; the only effect of skipping the refresh is that tenant-role claims are not updated
        // until their next full login. Social providers refresh normally below.
        ClaimsIdentity identity = (ClaimsIdentity)User.Identity!;
        if (provider != AuthProviderType.CustomOidc)
        {
            bool refreshed = await SocialAuthEvents.PopulateUserClaimsAsync(identity, HttpContext, ct, provider);
            if (refreshed)
            {
                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(identity));
            }
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
