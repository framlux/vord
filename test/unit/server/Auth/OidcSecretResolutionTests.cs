// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Security;
using NSubstitute;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests that <see cref="SsoOidcEvents.ResolveClientSecret"/> requires a protected secret and
/// rejects any unprotected (plaintext) value outright.
/// </summary>
public sealed class OidcSecretResolutionTests
{
    [Test]
    public async Task ResolveClientSecret_Plaintext_Throws()
    {
        IOidcSecretProtector protector = Substitute.For<IOidcSecretProtector>();
        protector.IsProtected("plain").Returns(false);

        await Assert.That(() => SsoOidcEvents.ResolveClientSecret(protector, "plain"))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ResolveClientSecret_Protected_Unprotects()
    {
        IOidcSecretProtector protector = Substitute.For<IOidcSecretProtector>();
        protector.IsProtected("prot1:xyz").Returns(true);
        protector.Unprotect("prot1:xyz").Returns("secret");

        string result = SsoOidcEvents.ResolveClientSecret(protector, "prot1:xyz");

        await Assert.That(result).IsEqualTo("secret");
    }
}
