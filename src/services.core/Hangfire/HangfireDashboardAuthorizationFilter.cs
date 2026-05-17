// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Services.Core.Hangfire;

/// <summary>
/// Authorizes Hangfire dashboard access to system-wide administrators (users with the "iga" claim
/// set to true). Matches the existing "Admin" authorization policy in server/Program.cs.
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
    /// types whose constructors and namespaces drift across versions. System-wide administrators
    /// are identified by the "iga" (is-global-admin) claim set to <see cref="bool.TrueString"/>.
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

        string? iga = httpContext.User.FindFirstValue("iga");

        return string.Equals(iga, bool.TrueString, StringComparison.OrdinalIgnoreCase);
    }
}
