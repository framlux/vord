// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Services.Core.Billing;

namespace Framlux.FleetManagement.Test.Services.Billing;

public sealed class StripeBilledTiersTests
{
    [Test]
    public async Task StripeBilledTiers_AreExactlyProAndTeam()
    {
        await Assert.That(StripeBilledTiers.All).IsEquivalentTo([SubscriptionTier.Pro, SubscriptionTier.Team]);

        foreach (SubscriptionTier tier in Enum.GetValues<SubscriptionTier>())
        {
            bool expected = (tier == SubscriptionTier.Pro) || (tier == SubscriptionTier.Team);
            await Assert.That(StripeBilledTiers.Contains(tier)).IsEqualTo(expected).Because($"tier {tier}");
        }
    }
}
