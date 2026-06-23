// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Auth;

namespace Framlux.FleetManagement.Test.Auth;

/// <summary>
/// Tests for <see cref="SecurityStampClaims"/> claim-name contract.
/// </summary>
public sealed class SecurityStampClaimsTests
{
    [Test]
    public async Task SecurityStampClaim_HasStableName()
    {
        await Assert.That(SecurityStampClaims.SecurityStampClaim).IsEqualTo("sst");
    }

    [Test]
    public async Task AuthProviderClaim_HasStableName()
    {
        await Assert.That(SecurityStampClaims.AuthProviderClaim).IsEqualTo("apr");
    }
}
