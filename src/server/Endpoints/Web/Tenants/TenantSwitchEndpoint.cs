// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Tenants;

/// <summary>
/// Request model for switching the active tenant.
/// </summary>
public sealed class TenantSwitchRequest
{
    /// <summary>
    /// The tenant ID to switch to.
    /// </summary>
    public int TenantId { get; set; }
}

/// <summary>
/// Switches the active tenant for the current user.
/// </summary>
public sealed class TenantSwitchEndpoint : Endpoint<TenantSwitchRequest, ApiResponse<object>>
{
    private readonly AuthCookieOptions _authCookieOptions;

    /// <summary>
    /// Creates a new instance of the <see cref="TenantSwitchEndpoint"/> class.
    /// </summary>
    public TenantSwitchEndpoint(IOptions<AuthCookieOptions> authCookieOptions)
    {
        _authCookieOptions = authCookieOptions.Value;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/tenants/switch");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(TenantSwitchRequest req, CancellationToken ct)
    {
        // Validate user has a role for the requested tenant
        bool hasAccess = User.FindAll(ClaimTypes.Role)
            .Any(c => c.Value.StartsWith($"{req.TenantId}:"));

        if (hasAccess == false)
        {
            HttpContext.Response.StatusCode = 403;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<object>.Error("You do not have access to this tenant"), ct);

            return;
        }

        string cookieDomain = _authCookieOptions.CookieDomain;
        HttpContext.Response.Cookies.Append("vord_tenant", req.TenantId.ToString(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = true,
            Path = "/",
            MaxAge = TimeSpan.FromDays(31),
            Domain = string.IsNullOrEmpty(cookieDomain) == false ? cookieDomain : null,
        });

        await Send.OkAsync(ApiResponse<object>.Ok(new { }, "Tenant switched"), cancellation: ct);
    }
}
