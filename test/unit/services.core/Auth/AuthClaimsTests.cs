// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using Framlux.FleetManagement.Services.Core.Auth;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests for <see cref="AuthClaims"/>. Locks down the case-insensitive comparison contract and the
/// null-input guard for the centralized iga-claim check.
/// </summary>
public sealed class AuthClaimsTests
{
    private static ClaimsPrincipal PrincipalWithIga(string? value)
    {
        ClaimsIdentity identity = new("Cookie");
        if (value is not null)
        {
            identity.AddClaim(new Claim(AuthClaims.IsGlobalAdmin, value));
        }

        return new ClaimsPrincipal(identity);
    }

    [Test]
    public async Task IsUserGlobalAdmin_TrueValue_ReturnsTrue()
    {
        bool result = AuthClaims.IsUserGlobalAdmin(PrincipalWithIga("True"));

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsUserGlobalAdmin_LowercaseTrue_ReturnsTrue()
    {
        bool result = AuthClaims.IsUserGlobalAdmin(PrincipalWithIga("true"));

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsUserGlobalAdmin_UpperCaseTRUE_ReturnsTrue()
    {
        bool result = AuthClaims.IsUserGlobalAdmin(PrincipalWithIga("TRUE"));

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task IsUserGlobalAdmin_FalseValue_ReturnsFalse()
    {
        bool result = AuthClaims.IsUserGlobalAdmin(PrincipalWithIga("False"));

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUserGlobalAdmin_ClaimAbsent_ReturnsFalse()
    {
        bool result = AuthClaims.IsUserGlobalAdmin(PrincipalWithIga(null));

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUserGlobalAdmin_EmptyValue_ReturnsFalse()
    {
        bool result = AuthClaims.IsUserGlobalAdmin(PrincipalWithIga(string.Empty));

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUserGlobalAdmin_ArbitraryValue_ReturnsFalse()
    {
        bool result = AuthClaims.IsUserGlobalAdmin(PrincipalWithIga("yes"));

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task IsUserGlobalAdmin_NullPrincipal_ThrowsArgumentNullException()
    {
        ArgumentNullException? ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
        {
            AuthClaims.IsUserGlobalAdmin(null!);

            return Task.CompletedTask;
        });

        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.ParamName).IsEqualTo("user");
    }

}
