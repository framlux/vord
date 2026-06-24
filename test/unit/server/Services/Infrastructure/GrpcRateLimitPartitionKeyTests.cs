// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Security.Claims;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Test.Services;

/// <summary>Tests the post-auth gRPC rate-limit partition-key derivation.</summary>
public class GrpcRateLimitPartitionKeyTests
{
    private static ClaimsPrincipal PrincipalWith(params (string Type, string Value)[] claims)
    {
        ClaimsIdentity identity = new(claims.Select(c => new Claim(c.Type, c.Value)), "test");

        return new ClaimsPrincipal(identity);
    }

    [Test]
    public async Task Derive_AuthenticatedMachine_KeysOnMachineId()
    {
        ClaimsPrincipal user = PrincipalWith(("MachineId", "4242"), ("TenantId", "7"));
        string key = GrpcRateLimitPartitionKey.Derive(user, "10.0.0.9");
        await Assert.That(key).IsEqualTo("machine:4242");
    }

    [Test]
    public async Task Derive_AuthenticatedTenantWithoutMachine_KeysOnTenant()
    {
        ClaimsPrincipal user = PrincipalWith(("TenantId", "7"));
        string key = GrpcRateLimitPartitionKey.Derive(user, "10.0.0.9");
        await Assert.That(key).IsEqualTo("tenant:7");
    }

    [Test]
    public async Task Derive_Unauthenticated_FallsBackToIp()
    {
        ClaimsPrincipal user = PrincipalWith();
        string key = GrpcRateLimitPartitionKey.Derive(user, "10.0.0.9");
        await Assert.That(key).IsEqualTo("ip:10.0.0.9");
    }

    [Test]
    public async Task Derive_NullPrincipal_FallsBackToIp()
    {
        string key = GrpcRateLimitPartitionKey.Derive(null, "10.0.0.9");
        await Assert.That(key).IsEqualTo("ip:10.0.0.9");
    }
}
