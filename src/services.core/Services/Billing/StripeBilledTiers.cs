// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// The tiers billed through Stripe. This is a billing question, not a feature question, and is
/// deliberately kept apart from <see cref="SubscriptionPolicy"/>: Enterprise has every Team feature
/// but is invoiced outside Stripe, so nothing that pushes quantities, reconciles or charges may ever
/// treat it as billable.
/// </summary>
public static class StripeBilledTiers
{
    /// <summary>The Stripe-billed tiers.</summary>
    public static IReadOnlyList<SubscriptionTier> All { get; } = [SubscriptionTier.Pro, SubscriptionTier.Team];

    /// <summary>Whether a tier is billed through Stripe.</summary>
    /// <param name="tier">The tier to test.</param>
    /// <returns><c>true</c> for Pro and Team.</returns>
    public static bool Contains(SubscriptionTier tier)
    {
        return (tier == SubscriptionTier.Pro) || (tier == SubscriptionTier.Team);
    }
}
