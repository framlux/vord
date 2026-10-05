// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Server.Endpoints.Grpc;
using Framlux.Vord.BillingGrpc;

namespace Framlux.FleetManagement.UnitTest.Endpoints.Grpc;

/// <summary>
/// The fleet's tier enum and the billing contract's tier enum must agree value for value, because
/// billing-api and the fleet exchange tiers as integers on the wire. A tier added on one side only
/// is read as undefined by the other.
/// </summary>
public sealed class SubscriptionTierParityTests
{
    [Test]
    public async Task EveryDomainTier_HasAProtoTierWithTheSameValue()
    {
        foreach (SubscriptionTier tier in Enum.GetValues<SubscriptionTier>())
        {
            // Value 0 is None in the domain and Unspecified in the contract: same meaning, different name.
            if (tier == SubscriptionTier.None)
            {
                continue;
            }

            BillingTier proto = (BillingTier)(int)tier;

            await Assert.That(Enum.IsDefined(proto)).IsTrue().Because($"domain tier {tier} has no contract value");
            await Assert.That(proto.ToString()).IsEqualTo(tier.ToString());
        }
    }

    [Test]
    public async Task EveryProtoTier_HasADomainTierWithTheSameValue()
    {
        foreach (BillingTier proto in Enum.GetValues<BillingTier>())
        {
            if (proto == BillingTier.Unspecified)
            {
                continue;
            }

            SubscriptionTier tier = (SubscriptionTier)(int)proto;

            await Assert.That(Enum.IsDefined(tier)).IsTrue().Because($"contract tier {proto} has no domain value");
        }
    }

    [Test]
    [Arguments(SubscriptionTier.Free, BillingTier.Free)]
    [Arguments(SubscriptionTier.Pro, BillingTier.Pro)]
    [Arguments(SubscriptionTier.Team, BillingTier.Team)]
    [Arguments(SubscriptionTier.Enterprise, BillingTier.Enterprise)]
    public async Task FleetAdminMappers_RoundTripEveryTier(SubscriptionTier tier, BillingTier proto)
    {
        await Assert.That(FleetAdminService.MapSubscriptionTierToBillingTier(tier)).IsEqualTo(proto);
        await Assert.That(FleetAdminService.MapBillingTierToSubscriptionTier(proto)).IsEqualTo(tier);
    }
}
