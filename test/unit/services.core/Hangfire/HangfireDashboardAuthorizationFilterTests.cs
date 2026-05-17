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
///
/// The filter authorizes system-wide administrators identified by the "iga" (is-global-admin) claim
/// set to <see cref="bool.TrueString"/>, matching the existing "Admin" authorization policy in
/// server/Program.cs and the convention enforced by AllowedRolesHandler.
/// </summary>
public sealed class HangfireDashboardAuthorizationFilterTests
{
    [Test]
    public async Task AuthorizeHttpContext_AnonymousUser_ReturnsFalse()
    {
        HttpContext ctx = BuildHttpContext(isAuthenticated: false, isGlobalAdmin: false);

        bool result = HangfireDashboardAuthorizationFilter.AuthorizeHttpContext(ctx);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task AuthorizeHttpContext_AuthenticatedNonAdmin_ReturnsFalse()
    {
        HttpContext ctx = BuildHttpContext(isAuthenticated: true, isGlobalAdmin: false);

        bool result = HangfireDashboardAuthorizationFilter.AuthorizeHttpContext(ctx);

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task AuthorizeHttpContext_GlobalAdmin_ReturnsTrue()
    {
        HttpContext ctx = BuildHttpContext(isAuthenticated: true, isGlobalAdmin: true);

        bool result = HangfireDashboardAuthorizationFilter.AuthorizeHttpContext(ctx);

        await Assert.That(result).IsTrue();
    }

    private static HttpContext BuildHttpContext(bool isAuthenticated, bool isGlobalAdmin)
    {
        List<Claim> claims = new();
        if (isGlobalAdmin)
        {
            // Matches TestAuthHandler.cs and the existing "Admin" authorization policy in
            // server/Program.cs which both gate on Claim("iga", bool.TrueString).
            claims.Add(new Claim("iga", bool.TrueString));
        }

        ClaimsIdentity identity = isAuthenticated ? new ClaimsIdentity(claims, "test") : new ClaimsIdentity();
        ClaimsPrincipal principal = new(identity);
        DefaultHttpContext httpContext = new() { User = principal };

        return httpContext;
    }
}
