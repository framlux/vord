// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.Vord.BillingGrpc;

namespace Framlux.FleetManagement.UnitTest.Endpoints.Grpc;

/// <summary>
/// Guards that the domain <see cref="ServerConfigurationSettingKeys"/> enum and the proto-generated
/// <see cref="ServerSettingKey"/> enum stay in lockstep. If a new domain setting key is added (or
/// renumbered) without a matching change to the shared proto contract, the gRPC boundary silently
/// drops or misreads settings — this test fails loudly instead.
/// </summary>
public sealed class ServerSettingKeyParityTests
{
    /// <summary>
    /// Every domain setting key must have a proto counterpart with the same name (case-insensitive,
    /// since proto codegen may normalize casing such as "TTL" to "Ttl") and the same underlying int
    /// value, and vice versa.
    /// </summary>
    [Test]
    public async Task DomainAndContractSettingKeys_MatchMemberForMember()
    {
        Dictionary<string, int> domain = Enum.GetValues<ServerConfigurationSettingKeys>()
            .ToDictionary(k => k.ToString().ToLowerInvariant(), k => (int)k);
        Dictionary<string, int> contract = Enum.GetValues<ServerSettingKey>()
            .ToDictionary(k => k.ToString().ToLowerInvariant(), k => (int)k);

        List<string> domainNames = domain.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        List<string> contractNames = contract.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        await Assert.That(contractNames).IsEquivalentTo(domainNames);

        foreach (KeyValuePair<string, int> entry in domain)
        {
            await Assert.That(contract.ContainsKey(entry.Key)).IsTrue();
            await Assert.That(contract[entry.Key]).IsEqualTo(entry.Value);
        }
    }
}
