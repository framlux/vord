// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Auth;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Services.Core.Hangfire;

/// <summary>
/// Authorizes Hangfire dashboard access to system-wide administrators (users with the iga claim
/// set to true). Matches the Admin authorization policy in server/Program.cs via the shared
/// <see cref="AuthClaims.IsUserGlobalAdmin"/> helper.
/// </summary>
public sealed class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    /// <inheritdoc/>
    public bool Authorize(DashboardContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return AuthorizeHttpContext(context.GetHttpContext());
    }

    /// <summary>
    /// Pure authorization logic, exposed as an internal static method so it can be unit tested
    /// directly against a plain <see cref="HttpContext"/> without coupling to Hangfire dashboard
    /// types whose constructors and namespaces drift across versions. Delegates the iga-claim
    /// check to <see cref="AuthClaims.IsUserGlobalAdmin"/> so the contract is shared with the
    /// Admin policy and the cookie principal validator.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <returns>True if the user is authenticated and a global admin; false otherwise.</returns>
    internal static bool AuthorizeHttpContext(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        return AuthClaims.IsUserGlobalAdmin(httpContext.User);
    }
}
