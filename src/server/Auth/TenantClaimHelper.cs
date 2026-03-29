// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Server.Auth;

/// <summary>
/// Helper for extracting tenant ID from user claims and cookies.
/// </summary>
public static class TenantClaimHelper
{
    /// <summary>
    /// Extracts the tenant ID from the vord_tenant cookie (if valid) or falls back to the first role claim.
    /// Role claims are stored as "{tenantId}:{roleId}".
    /// </summary>
    /// <param name="user">The claims principal.</param>
    /// <param name="httpContext">The current HTTP context for reading cookies.</param>
    /// <returns>Returns the tenant ID if found; otherwise, null.</returns>
    public static int? GetTenantIdFromClaims(ClaimsPrincipal user, HttpContext httpContext)
    {
        // Collect all tenant IDs from role claims
        List<int> validTenantIds = [];
        foreach (Claim roleClaim in user.FindAll(ClaimTypes.Role))
        {
            if (roleClaim.Value.Contains(':'))
            {
                string tenantIdStr = roleClaim.Value.Split(':')[0];
                if (int.TryParse(tenantIdStr, out int tid))
                {
                    validTenantIds.Add(tid);
                }
            }
        }

        if (validTenantIds.Count == 0)
        {
            return null;
        }

        // Check vord_tenant cookie first
        string? cookieValue = httpContext.Request.Cookies["vord_tenant"];
        if (string.IsNullOrEmpty(cookieValue) == false && int.TryParse(cookieValue, out int cookieTenantId))
        {
            if (validTenantIds.Contains(cookieTenantId))
            {
                return cookieTenantId;
            }
        }

        // Fall back to first role claim

        return validTenantIds[0];
    }

    /// <summary>
    /// Extracts the tenant ID from the user's role claims (without cookie support).
    /// Role claims are stored as "{tenantId}:{roleId}".
    /// </summary>
    /// <param name="user">The claims principal.</param>
    /// <returns>Returns the tenant ID if found; otherwise, null.</returns>
    public static int? GetTenantIdFromClaims(ClaimsPrincipal user)
    {
        Claim? roleClaim = user.FindFirst(ClaimTypes.Role);
        if (roleClaim is not null && roleClaim.Value.Contains(':'))
        {
            string tenantIdStr = roleClaim.Value.Split(':')[0];
            if (int.TryParse(tenantIdStr, out int tid))
            {
                return tid;
            }
        }

        return null;
    }
}
