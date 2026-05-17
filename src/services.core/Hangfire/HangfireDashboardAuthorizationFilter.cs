// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Services.Core.Hangfire;

/// <summary>
/// Authorizes Hangfire dashboard access to users in the Admin role.
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
    /// types whose constructors and namespaces drift across versions.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <returns>True if the user is authenticated and in the Admin role; false otherwise.</returns>
    internal static bool AuthorizeHttpContext(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        return httpContext.User.IsInRole("Admin");
    }
}
