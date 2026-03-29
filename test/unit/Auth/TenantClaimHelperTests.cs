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
}
