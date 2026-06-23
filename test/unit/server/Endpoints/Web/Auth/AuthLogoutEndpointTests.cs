// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using Framlux.FleetManagement.Server.Endpoints.Web.Auth;
using Framlux.FleetManagement.Services.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Endpoints.Web.Auth;

/// <summary>
/// Tests for the security-stamp bump logic used by
/// <see cref="Framlux.FleetManagement.Server.Endpoints.Web.Auth.AuthLogoutEndpoint"/>. Logout must
/// rotate the stamp when the user id is known, and skip the bump (without failing) when it is not.
/// </summary>
public sealed class AuthLogoutEndpointTests
{
    [Test]
    public async Task BumpSecurityStampAsync_ParseableActor_BumpsStamp()
    {
        IUserSecurityStampService stampService = Substitute.For<IUserSecurityStampService>();
        ClaimsPrincipal principal = new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Actor, "42"),
        }, "test"));

        await AuthLogoutEndpoint.BumpSecurityStampAsync(
            principal, stampService, NullLogger.Instance, CancellationToken.None);

        await stampService.Received(1).BumpAsync(42, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BumpSecurityStampAsync_NonParseableActor_DoesNotBump()
    {
        IUserSecurityStampService stampService = Substitute.For<IUserSecurityStampService>();
        ClaimsPrincipal principal = new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Actor, "not-a-number"),
        }, "test"));

        await AuthLogoutEndpoint.BumpSecurityStampAsync(
            principal, stampService, NullLogger.Instance, CancellationToken.None);

        await stampService.DidNotReceive().BumpAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task BumpSecurityStampAsync_MissingActorClaim_DoesNotBump()
    {
        IUserSecurityStampService stampService = Substitute.For<IUserSecurityStampService>();
        ClaimsPrincipal principal = new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "ext-123"),
        }, "test"));

        await AuthLogoutEndpoint.BumpSecurityStampAsync(
            principal, stampService, NullLogger.Instance, CancellationToken.None);

        await stampService.DidNotReceive().BumpAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
