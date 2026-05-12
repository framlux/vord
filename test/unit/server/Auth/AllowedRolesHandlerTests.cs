// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Server.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests for <see cref="AllowedRolesHandler"/>.
/// </summary>
public class AllowedRolesHandlerTests
{
    private static IHttpContextAccessor CreateAccessor(string? tenantCookie = null)
    {
        DefaultHttpContext httpContext = new();
        if (tenantCookie is not null)
        {
            httpContext.Request.Headers.Append("Cookie", $"vord_tenant={tenantCookie}");
        }

        HttpContextAccessor accessor = new()
        {
            HttpContext = httpContext
        };

        return accessor;
    }

    private static AuthorizationHandlerContext BuildContext(
        AllowedRolesRequirement requirement,
        ClaimsPrincipal user)
    {
        return new AuthorizationHandlerContext(
            [requirement],
            user,
            resource: null);
    }

    [Test]
    public async Task GlobalAdmin_BypassesRoleCheck()
    {
        AllowedRolesHandler handler = new(CreateAccessor());
        AllowedRolesRequirement requirement = new(UserAccountRoles.Viewer);
        ClaimsIdentity identity = new(
            [new Claim("iga", "True")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);
        AuthorizationHandlerContext context = BuildContext(requirement, user);

        await handler.HandleAsync(context);

        await Assert.That(context.HasSucceeded).IsTrue();
    }

    [Test]
    public async Task CorrectRole_WithMatchingTenantCookie_Succeeds()
    {
        AllowedRolesHandler handler = new(CreateAccessor("5"));
        AllowedRolesRequirement requirement = new(UserAccountRoles.TenantAdmin);
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "5:1")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);
        AuthorizationHandlerContext context = BuildContext(requirement, user);

        await handler.HandleAsync(context);

        await Assert.That(context.HasSucceeded).IsTrue();
    }

    [Test]
    public async Task CorrectRole_WithInvalidTenantCookie_FallsBackToRoleClaim_Succeeds()
    {
        // Cookie points to tenant 99 which the user doesn't belong to;
        // TenantClaimHelper falls back to first role claim (tenant 5)
        AllowedRolesHandler handler = new(CreateAccessor("99"));
        AllowedRolesRequirement requirement = new(UserAccountRoles.TenantAdmin);
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "5:1")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);
        AuthorizationHandlerContext context = BuildContext(requirement, user);

        await handler.HandleAsync(context);

        await Assert.That(context.HasSucceeded).IsTrue();
    }

    [Test]
    public async Task CorrectRole_WithNoTenantCookie_FallsBackToRoleClaim_Succeeds()
    {
        AllowedRolesHandler handler = new(CreateAccessor());
        AllowedRolesRequirement requirement = new(UserAccountRoles.TenantAdmin);
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "5:1")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);
        AuthorizationHandlerContext context = BuildContext(requirement, user);

        await handler.HandleAsync(context);

        await Assert.That(context.HasSucceeded).IsTrue();
    }

    [Test]
    public async Task NoTenantCookie_NoRoleClaims_Fails()
    {
        AllowedRolesHandler handler = new(CreateAccessor());
        AllowedRolesRequirement requirement = new(UserAccountRoles.TenantAdmin);
        ClaimsIdentity identity = new(
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);
        AuthorizationHandlerContext context = BuildContext(requirement, user);

        await handler.HandleAsync(context);

        await Assert.That(context.HasSucceeded).IsFalse();
    }

    [Test]
    public async Task NoTenantCookie_MultipleRoles_UsesFirstTenant()
    {
        AllowedRolesHandler handler = new(CreateAccessor());
        AllowedRolesRequirement requirement = new(UserAccountRoles.Viewer);
        ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.Role, "1:3"),
                new Claim(ClaimTypes.Role, "2:1")
            ],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);
        AuthorizationHandlerContext context = BuildContext(requirement, user);

        await handler.HandleAsync(context);

        // Falls back to first role claim (tenant 1, role Viewer=3), which matches the requirement
        await Assert.That(context.HasSucceeded).IsTrue();
    }

    [Test]
    public async Task WrongRole_Fails()
    {
        AllowedRolesHandler handler = new(CreateAccessor("5"));
        AllowedRolesRequirement requirement = new(UserAccountRoles.TenantAdmin);
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "5:3")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);
        AuthorizationHandlerContext context = BuildContext(requirement, user);

        await handler.HandleAsync(context);

        await Assert.That(context.HasSucceeded).IsFalse();
    }

    [Test]
    public async Task MultipleRoles_SucceedsOnMatchForActiveTenant()
    {
        AllowedRolesHandler handler = new(CreateAccessor("2"));
        AllowedRolesRequirement requirement = new(UserAccountRoles.MachineAdmin);
        ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.Role, "1:3"),
                new Claim(ClaimTypes.Role, "2:2")
            ],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);
        AuthorizationHandlerContext context = BuildContext(requirement, user);

        await handler.HandleAsync(context);

        await Assert.That(context.HasSucceeded).IsTrue();
    }

    [Test]
    public async Task MultipleRoles_FailsWhenRoleInWrongTenant()
    {
        AllowedRolesHandler handler = new(CreateAccessor("1"));
        AllowedRolesRequirement requirement = new(UserAccountRoles.MachineAdmin);
        ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.Role, "1:3"),
                new Claim(ClaimTypes.Role, "2:2")
            ],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);
        AuthorizationHandlerContext context = BuildContext(requirement, user);

        await handler.HandleAsync(context);

        await Assert.That(context.HasSucceeded).IsFalse();
    }

    [Test]
    public async Task MalformedClaim_NoColonSeparator_Fails()
    {
        AllowedRolesHandler handler = new(CreateAccessor("5"));
        AllowedRolesRequirement requirement = new(UserAccountRoles.TenantAdmin);
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "garbage")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);
        AuthorizationHandlerContext context = BuildContext(requirement, user);

        await handler.HandleAsync(context);

        await Assert.That(context.HasSucceeded).IsFalse();
    }

    [Test]
    public async Task MalformedClaim_NonNumericRole_Fails()
    {
        AllowedRolesHandler handler = new(CreateAccessor("5"));
        AllowedRolesRequirement requirement = new(UserAccountRoles.TenantAdmin);
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "5:abc")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);
        AuthorizationHandlerContext context = BuildContext(requirement, user);

        await handler.HandleAsync(context);

        await Assert.That(context.HasSucceeded).IsFalse();
    }

    [Test]
    public async Task MalformedClaim_UndefinedRoleValue_Fails()
    {
        AllowedRolesHandler handler = new(CreateAccessor("5"));
        AllowedRolesRequirement requirement = new(UserAccountRoles.TenantAdmin);
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "5:99")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);
        AuthorizationHandlerContext context = BuildContext(requirement, user);

        await handler.HandleAsync(context);

        await Assert.That(context.HasSucceeded).IsFalse();
    }

    [Test]
    public async Task UnauthenticatedUser_Fails()
    {
        AllowedRolesHandler handler = new(CreateAccessor("5"));
        AllowedRolesRequirement requirement = new(UserAccountRoles.Viewer);
        ClaimsIdentity identity = new();
        ClaimsPrincipal user = new(identity);
        AuthorizationHandlerContext context = BuildContext(requirement, user);

        await handler.HandleAsync(context);

        await Assert.That(context.HasSucceeded).IsFalse();
    }

    [Test]
    public async Task EmptyRoleClaim_Fails()
    {
        AllowedRolesHandler handler = new(CreateAccessor("5"));
        AllowedRolesRequirement requirement = new(UserAccountRoles.TenantAdmin);
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, ":")],
            authenticationType: "test");
        ClaimsPrincipal user = new(identity);
        AuthorizationHandlerContext context = BuildContext(requirement, user);

        await handler.HandleAsync(context);

        await Assert.That(context.HasSucceeded).IsFalse();
    }
}
