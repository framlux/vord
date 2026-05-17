// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using Framlux.FleetManagement.Services.Core.Hangfire;
using Microsoft.AspNetCore.Http;

namespace Framlux.FleetManagement.Test.HangfireSupport;

/// <summary>
/// Tests for the pure authorization logic in <see cref="HangfireDashboardAuthorizationFilter"/>.
/// We exercise the internal-static helper directly to avoid coupling tests to Hangfire dashboard
/// types whose constructors and namespaces drift across Hangfire versions. Per CLAUDE.md:
/// extract non-trivial logic from framework-coupled entry points into internal static methods
/// so they can be unit tested directly.
/// </summary>
public sealed class HangfireDashboardAuthorizationFilterTests
{
    [Test]
    public async Task AuthorizeHttpContext_AnonymousUser_ReturnsFalse()
    {
        HttpContext ctx = BuildHttpContext(isAuthenticated: false, isAdmin: false);

        bool result = HangfireDashboardAuthorizationFilter.AuthorizeHttpContext(ctx);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task AuthorizeHttpContext_AuthenticatedNonAdmin_ReturnsFalse()
    {
        HttpContext ctx = BuildHttpContext(isAuthenticated: true, isAdmin: false);

        bool result = HangfireDashboardAuthorizationFilter.AuthorizeHttpContext(ctx);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task AuthorizeHttpContext_AdminUser_ReturnsTrue()
    {
        HttpContext ctx = BuildHttpContext(isAuthenticated: true, isAdmin: true);

        bool result = HangfireDashboardAuthorizationFilter.AuthorizeHttpContext(ctx);

        await Assert.That(result).IsTrue();
    }

    private static HttpContext BuildHttpContext(bool isAuthenticated, bool isAdmin)
    {
        List<Claim> claims = new();
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));
        }

        ClaimsIdentity identity = isAuthenticated ? new ClaimsIdentity(claims, "test") : new ClaimsIdentity();
        ClaimsPrincipal principal = new(identity);
        DefaultHttpContext httpContext = new() { User = principal };

        return httpContext;
    }
}
