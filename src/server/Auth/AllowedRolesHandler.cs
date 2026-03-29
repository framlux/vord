// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using Framlux.FleetManagement.Database.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Authorization handler for the <see cref="AllowedRolesRequirement"/>.
/// </summary>
public sealed class AllowedRolesHandler : AuthorizationHandler<AllowedRolesRequirement>
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>
    /// Initializes a new instance of the <see cref="AllowedRolesHandler"/> class.
    /// </summary>
    /// <param name="httpContextAccessor">The HTTP context accessor for reading cookies.</param>
    public AllowedRolesHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Handles the authorization requirement for allowed user roles.
    /// </summary>
    /// <param name="context">The authorization handler context.</param>
    /// <param name="requirement">The authorization requirement.</param>
    /// <returns>Returns an awaitable Task</returns>
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AllowedRolesRequirement requirement)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            return Task.CompletedTask;
        }

        // Global admin bypass
        string? iga = context.User.FindFirstValue("iga");
        if (string.Equals(iga, bool.TrueString, StringComparison.OrdinalIgnoreCase))
        {
            context.Succeed(requirement);

            return Task.CompletedTask;
        }

        // Determine the active tenant from the vord_tenant cookie
        int? activeTenantId = GetActiveTenantId();
        if (activeTenantId is null)
        {
            return Task.CompletedTask;
        }

        // Find the user's role claim scoped to the active tenant
        IEnumerable<string> roles = context.User.FindAll(ClaimTypes.Role).Select(c => c.Value);
        foreach (string roleClaim in roles)
        {
            // Claims are <tenantId>:<roleValue>
            string[] parts = roleClaim.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            // Only check roles for the active tenant
            if (int.TryParse(parts[0], out int tenantId) == false || tenantId != activeTenantId.Value)
            {
                continue;
            }

            // Parse role and check if allowed
            if (byte.TryParse(parts[1], out byte b))
            {
                // Make sure this is a defined role
                UserAccountRoles role = (UserAccountRoles)b;
                if (Enum.IsDefined<UserAccountRoles>(role) == false)
                {
                    continue;
                }

                // Check if role is allowed
                if (requirement.Allowed.Contains(role))
                {
                    context.Succeed(requirement);

                    return Task.CompletedTask;
                }
            }
        }

        return Task.CompletedTask;
    }

    private int? GetActiveTenantId()
    {
        HttpContext? httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return null;
        }

        string? cookieValue = httpContext.Request.Cookies["vord_tenant"];
        if (string.IsNullOrEmpty(cookieValue) == false && int.TryParse(cookieValue, out int tenantId))
        {
            return tenantId;
        }

        return null;
    }
}
