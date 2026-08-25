// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Billing;

namespace Framlux.FleetManagement.Test.Services.Billing;

/// <summary>
/// Tests for <see cref="SubscriptionPolicy"/>. These are pure functions over a loaded subscription,
/// so the coverage is a truth table across every tier and status rather than a set of scenarios.
/// Every predicate is block-polarity — true means refuse — and every one fails closed on null,
/// which is the property the hand-written copies these replaced did not all share.
/// </summary>
public sealed class SubscriptionPolicyTests
{
    private static TenantSubscription Subscription(SubscriptionTier tier, SubscriptionStatus status)
    {
        return new TenantSubscription
        {
            TenantId = 1,
            Tier = tier,
            Status = status,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
    }

    private static IEnumerable<SubscriptionTier> AllTiers()
    {
        return Enum.GetValues<SubscriptionTier>();
    }

    private static IEnumerable<SubscriptionStatus> AllStatuses()
    {
        return Enum.GetValues<SubscriptionStatus>();
    }

    [Test]
    public async Task EveryPredicate_FailsClosedOnNull()
    {
        await Assert.That(SubscriptionPolicy.RequiresPro(null)).IsTrue();
        await Assert.That(SubscriptionPolicy.RequiresTeam(null)).IsTrue();
        await Assert.That(SubscriptionPolicy.RequiresPaidTier(null)).IsTrue();
        await Assert.That(SubscriptionPolicy.BlocksMutations(null)).IsTrue();
    }

    /// <summary>
    /// The Pro gate is the only one that consults status: a paid tier that has lapsed is refused
    /// exactly as a Free tier is.
    /// </summary>
    [Test]
    public async Task RequiresPro_BlocksFreeTierAndAnyNonActiveStatus()
    {
        foreach (SubscriptionTier tier in AllTiers())
        {
            foreach (SubscriptionStatus status in AllStatuses())
            {
                bool expected = (tier == SubscriptionTier.Free) ||
                                (status != SubscriptionStatus.Active);

                await Assert.That(SubscriptionPolicy.RequiresPro(Subscription(tier, status)))
                    .IsEqualTo(expected)
                    .Because($"tier {tier} with status {status}");
            }
        }
    }

    /// <summary>
    /// Pins a pre-existing quirk rather than endorsing it: the tier test names Free explicitly, so
    /// the unset default None passes the Pro gate whenever the status is Active. The predicate was
    /// moved here verbatim, and tightening it during a consolidation would change behaviour on
    /// seven endpoints at once. Fix it as its own change if it is worth fixing.
    /// </summary>
    [Test]
    public async Task RequiresPro_AllowsUnsetTierWhenActive()
    {
        await Assert.That(SubscriptionPolicy.RequiresPro(
            Subscription(SubscriptionTier.None, SubscriptionStatus.Active))).IsFalse();
    }

    [Test]
    public async Task RequiresPro_AllowsActiveProAndTeam()
    {
        await Assert.That(SubscriptionPolicy.RequiresPro(
            Subscription(SubscriptionTier.Pro, SubscriptionStatus.Active))).IsFalse();
        await Assert.That(SubscriptionPolicy.RequiresPro(
            Subscription(SubscriptionTier.Team, SubscriptionStatus.Active))).IsFalse();
    }

    /// <summary>
    /// The Team gate is tier-only by design — every hand-written copy it replaces tested tier
    /// alone, so consulting status here would tighten nine gates during a refactor whose contract
    /// is bit-for-bit behaviour preservation.
    /// </summary>
    [Test]
    public async Task RequiresTeam_BlocksEveryTierExceptTeam_RegardlessOfStatus()
    {
        foreach (SubscriptionTier tier in AllTiers())
        {
            foreach (SubscriptionStatus status in AllStatuses())
            {
                bool expected = tier != SubscriptionTier.Team;

                await Assert.That(SubscriptionPolicy.RequiresTeam(Subscription(tier, status)))
                    .IsEqualTo(expected)
                    .Because($"tier {tier} with status {status}");
            }
        }
    }

    /// <summary>
    /// The invitation rule: any paid tier passes, and status is not consulted. It is neither the
    /// Pro predicate (which tests status) nor the Team one (which refuses Pro).
    /// </summary>
    [Test]
    public async Task RequiresPaidTier_BlocksOnlyFree_RegardlessOfStatus()
    {
        foreach (SubscriptionTier tier in AllTiers())
        {
            foreach (SubscriptionStatus status in AllStatuses())
            {
                bool expected = tier == SubscriptionTier.Free;

                await Assert.That(SubscriptionPolicy.RequiresPaidTier(Subscription(tier, status)))
                    .IsEqualTo(expected)
                    .Because($"tier {tier} with status {status}");
            }
        }
    }

    /// <summary>
    /// Deliberately not named IsActive: it returns true for Canceled only, so the mutation gate
    /// stays exactly as permissive as it was for PastDue, which is a state the product keeps
    /// serving while payment is retried.
    /// </summary>
    [Test]
    public async Task BlocksMutations_BlocksCanceledOnly_RegardlessOfTier()
    {
        foreach (SubscriptionTier tier in AllTiers())
        {
            foreach (SubscriptionStatus status in AllStatuses())
            {
                bool expected = status == SubscriptionStatus.Canceled;

                await Assert.That(SubscriptionPolicy.BlocksMutations(Subscription(tier, status)))
                    .IsEqualTo(expected)
                    .Because($"tier {tier} with status {status}");
            }
        }
    }

    /// <summary>
    /// PastDue keeps mutating: the account is behind on payment, not closed, and locking it out
    /// would be a behaviour change rather than the consolidation this type exists for.
    /// </summary>
    [Test]
    public async Task BlocksMutations_AllowsPastDue()
    {
        await Assert.That(SubscriptionPolicy.BlocksMutations(
            Subscription(SubscriptionTier.Pro, SubscriptionStatus.PastDue))).IsFalse();
    }
}
