// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
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
    private readonly ITenantRepository _tenantRepository;

    /// <summary>
    /// Creates a new instance of the <see cref="TenantSwitchEndpoint"/> class.
    /// </summary>
    public TenantSwitchEndpoint(IOptions<AuthCookieOptions> authCookieOptions, ITenantRepository tenantRepository)
    {
        _authCookieOptions = authCookieOptions.Value;
        _tenantRepository = tenantRepository;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Post("/tenants/switch");
        Version(1);
    }

    /// <summary>
    /// Decides whether a switch to the requested tenant is allowed. A switch requires both an
    /// active membership row for the user in that tenant and the tenant itself being active —
    /// a deactivated tenant (Phase-1 tenant deletion) must not be switchable into even though its
    /// <see cref="UserTenantRole"/> rows survive until the purge.
    /// </summary>
    /// <param name="isMember">Whether the user has an active role in the requested tenant.</param>
    /// <param name="tenantIsActive">Whether the requested tenant is active (not deactivated).</param>
    internal static bool CanSwitchToTenant(bool isMember, bool tenantIsActive)
    {
        return isMember && tenantIsActive;
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(TenantSwitchRequest req, CancellationToken ct)
    {
        string? userIdValue = User.FindFirstValue(ClaimTypes.Actor);
        if ((userIdValue is null) || (int.TryParse(userIdValue, out int userId) == false))
        {
            await HttpContext.SendApiErrorAsync(401, "Unauthorized", ct);

            return;
        }

        // Resolve membership and tenant state from the database rather than trusting the role
        // claims on the principal: those claims can lag a deactivation by up to the role-claim
        // cache TTL, and this switch decision must be authoritative at the moment it is made.
        UserAccountRoles? activeRole = await _tenantRepository.GetActiveUserRoleAsync(userId, req.TenantId, ct);
        bool isMember = activeRole.HasValue;

        Tenant? tenant = await _tenantRepository.GetTenantByIdAsync(req.TenantId, ct);
        bool tenantIsActive = tenant is not null;

        if (CanSwitchToTenant(isMember, tenantIsActive) == false)
        {
            await HttpContext.SendApiErrorAsync(403, "You do not have access to this tenant", ct);

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
