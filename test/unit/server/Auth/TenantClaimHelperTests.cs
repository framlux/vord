// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Auth;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests for <see cref="TenantClaimHelper"/>.
/// </summary>
public class TenantClaimHelperTests
{
    [Test]
    public async Task GetTenantIdFromClaims_WithoutHttpContext_ExtractsFromFirstRoleClaim()
    {
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "42:1")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user);

        await Assert.That(result).IsEqualTo(42);
    }

    [Test]
    public async Task GetTenantIdFromClaims_WithoutHttpContext_NoClaims_ReturnsNull()
    {
        ClaimsIdentity identity = new(authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetTenantIdFromClaims_WithoutHttpContext_MalformedClaim_ReturnsNull()
    {
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "not-valid")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetTenantIdFromClaims_WithoutHttpContext_NonNumericTenantId_ReturnsNull()
    {
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "abc:1")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetTenantIdFromClaims_WithHttpContext_CookieOverridesDefault()
    {
        ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.Role, "10:1"),
                new Claim(ClaimTypes.Role, "20:2")
            ],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers["Cookie"] = "vord_tenant=20";

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user, httpContext);

        await Assert.That(result).IsEqualTo(20);
    }

    [Test]
    public async Task GetTenantIdFromClaims_WithHttpContext_InvalidCookie_FallsBackToFirstClaim()
    {
        ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.Role, "10:1"),
                new Claim(ClaimTypes.Role, "20:2")
            ],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers["Cookie"] = "vord_tenant=999";

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user, httpContext);

        await Assert.That(result).IsEqualTo(10);
    }

    [Test]
    public async Task GetTenantIdFromClaims_WithHttpContext_NoCookie_FallsBackToFirstClaim()
    {
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "15:3")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        DefaultHttpContext httpContext = new();

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user, httpContext);

        await Assert.That(result).IsEqualTo(15);
    }

    [Test]
    public async Task GetTenantIdFromClaims_WithHttpContext_NoClaims_ReturnsNull()
    {
        ClaimsIdentity identity = new(authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        DefaultHttpContext httpContext = new();

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user, httpContext);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetTenantIdFromClaims_WithHttpContext_NonNumericCookie_FallsBackToFirstClaim()
    {
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "7:1")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers["Cookie"] = "vord_tenant=abc";

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user, httpContext);

        await Assert.That(result).IsEqualTo(7);
    }

    [Test]
    public async Task GetTenantIdFromClaims_WithHttpContext_ClaimWithoutColon_Skipped()
    {
        ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.Role, "malformed"),
                new Claim(ClaimTypes.Role, "5:1")
            ],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        DefaultHttpContext httpContext = new();

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user, httpContext);

        await Assert.That(result).IsEqualTo(5);
    }

    [Test]
    public async Task GetTenantIdFromClaims_WithHttpContext_NonNumericTenantIdInClaim_IsSkipped()
    {
        // Claims where tenant ID portion before ':' is non-numeric must be skipped.
        // Only the valid numeric claim should be returned.
        ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.Role, "abc:1"),
                new Claim(ClaimTypes.Role, "99:2")
            ],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        DefaultHttpContext httpContext = new();

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user, httpContext);

        await Assert.That(result).IsEqualTo(99);
    }

    [Test]
    public async Task GetTenantIdFromClaims_WithHttpContext_AllClaimsNonNumericTenantId_ReturnsNull()
    {
        // When every role claim has a non-numeric tenant ID portion, no valid tenant IDs are collected.
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "abc:1")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        DefaultHttpContext httpContext = new();

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user, httpContext);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetUserIdFromClaims_ValidActorClaim_ReturnsUserId()
    {
        // A positive integer Actor claim must be extracted as the user ID.
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Actor, "42")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        int? result = TenantClaimHelper.GetUserIdFromClaims(user);

        await Assert.That(result).IsEqualTo(42);
    }

    [Test]
    public async Task GetUserIdFromClaims_NoActorClaim_ReturnsNull()
    {
        // When no Actor claim is present the user ID cannot be determined.
        ClaimsIdentity identity = new(authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        int? result = TenantClaimHelper.GetUserIdFromClaims(user);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetUserIdFromClaims_EmptyActorClaim_ReturnsNull()
    {
        // An empty Actor claim value is treated the same as absent — no user ID.
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Actor, "")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        int? result = TenantClaimHelper.GetUserIdFromClaims(user);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetUserIdFromClaims_NonNumericActorClaim_ReturnsNull()
    {
        // A non-numeric Actor claim value cannot be parsed as a user ID.
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Actor, "not-a-number")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        int? result = TenantClaimHelper.GetUserIdFromClaims(user);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetUserIdFromClaims_ZeroActorClaim_ReturnsNull()
    {
        // User IDs must be positive; zero is not a valid user ID.
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Actor, "0")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        int? result = TenantClaimHelper.GetUserIdFromClaims(user);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetUserIdFromClaims_NegativeActorClaim_ReturnsNull()
    {
        // Negative values are not valid user IDs and must be rejected.
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Actor, "-5")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        int? result = TenantClaimHelper.GetUserIdFromClaims(user);

        await Assert.That(result).IsNull();
    }

    // ==========================================================================================
    // L4 regression: the vord_tenant cookie can never grant cross-tenant access. The cookie is
    // only honored when its value is also present in the user's role claims; an unauthorized
    // tenant id MUST be silently ignored, falling back to the first claim's tenant.
    // ==========================================================================================

    [Test]
    public async Task GetTenantIdFromClaims_CookieNamesUnauthorizedTenant_FallsBackToAuthorized()
    {
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "10:1")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        DefaultHttpContext httpContext = new();
        // Tenant 999 is NOT in the user's role claims — attempting to act as it must be ignored.
        httpContext.Request.Headers["Cookie"] = "vord_tenant=999";

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user, httpContext);

        // Falls back to the only authorized tenant; does NOT honor the unauthorized cookie value.
        await Assert.That(result).IsEqualTo(10);
    }

    [Test]
    public async Task GetTenantIdFromClaims_CookieWithNegativeTenant_Ignored()
    {
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "10:1")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers["Cookie"] = "vord_tenant=-1";

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user, httpContext);

        await Assert.That(result).IsEqualTo(10);
    }

    [Test]
    public async Task GetTenantIdFromClaims_CookieWithNonNumeric_Ignored()
    {
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "42:1")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers["Cookie"] = "vord_tenant=admin";

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user, httpContext);

        await Assert.That(result).IsEqualTo(42);
    }

    [Test]
    public async Task GetTenantIdFromClaims_CookieMatchesAuthorized_HonorsCookie()
    {
        // Positive regression: when the cookie names a tenant the user IS authorized for, the
        // cookie value drives selection (user's active tenant). This is the legitimate
        // tenant-switching path and must not be broken by the isolation hardening.
        ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.Role, "10:1"),
                new Claim(ClaimTypes.Role, "20:2"),
            ],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers["Cookie"] = "vord_tenant=20";

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user, httpContext);

        await Assert.That(result).IsEqualTo(20);
    }

    [Test]
    public async Task GetTenantIdFromClaims_NoRoleClaims_ReturnsNullEvenIfCookieSet()
    {
        // A user with no role claims has no authorized tenants. Even a syntactically valid
        // cookie must NOT manufacture access.
        ClaimsIdentity identity = new(authenticationType: "test");
        ClaimsPrincipal user = new(identity);

        DefaultHttpContext httpContext = new();
        httpContext.Request.Headers["Cookie"] = "vord_tenant=10";

        int? result = TenantClaimHelper.GetTenantIdFromClaims(user, httpContext);

        await Assert.That(result).IsNull();
    }
}
