// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// The tier and status rules that decide whether a tenant may use a gated feature, expressed once.
/// These were previously hand-written at roughly twenty call sites, which meant adding a gated
/// feature depended on remembering to copy the idiom — and there is no test that can catch a gate
/// that was never written.
/// </summary>
/// <remarks>
/// <para>
/// Every predicate here is <b>block-polarity</b>: <c>true</c> means refuse. A single allow-polarity
/// name sitting among them is exactly the detail that gets misread once and then copied, so there
/// is deliberately not one. Call sites that ask the positive question — "is this tenant Team?" —
/// write <c>RequiresTeam(subscription) == false</c> rather than a fresh comparison.
/// </para>
/// <para>
/// Every predicate also fails closed on a missing subscription. That case is decided here, in the
/// open, so no caller has to remember it.
/// </para>
/// <para>
/// These are pure functions over an already-loaded subscription. Anything needing repository
/// access — count limits, ingest eligibility, retention — stays on ISubscriptionService, where the
/// self-hosted decorator can answer it permissively. The billable-tier allowlist is also
/// deliberately absent: it selects which tiers are invoiced, not which features are permitted, and
/// folding a billing concern in here would blur the boundary this type exists to sharpen.
/// </para>
/// </remarks>
public static class SubscriptionPolicy
{
    /// <summary>
    /// Whether the tenant must be refused a Pro-or-Team feature: no subscription, the Free tier, or
    /// any status other than Active.
    /// </summary>
    /// <param name="subscription">The tenant's subscription, or <c>null</c> if none exists.</param>
    /// <returns><c>true</c> when access must be denied.</returns>
    /// <remarks>
    /// This is the only predicate here that consults status, so a lapsed paid tier is refused
    /// exactly as a Free one is. Note it names Free rather than testing "not paid", so the unset
    /// default tier passes when the status is Active; that is the shape it has always had and was
    /// moved here unchanged.
    /// </remarks>
    public static bool RequiresPro(TenantSubscription? subscription)
    {
        return (subscription is null) ||
               (subscription.Tier == SubscriptionTier.Free) ||
               (subscription.Status != SubscriptionStatus.Active);
    }

    /// <summary>
    /// Whether the tenant must be refused a Team-only feature: no subscription, or any tier other
    /// than Team.
    /// </summary>
    /// <param name="subscription">The tenant's subscription, or <c>null</c> if none exists.</param>
    /// <returns><c>true</c> when access must be denied.</returns>
    /// <remarks>
    /// Tier-only, with no status test, because every hand-written copy this replaces was tier-only.
    /// Adding a status test here would tighten every Team gate at once.
    /// </remarks>
    public static bool RequiresTeam(TenantSubscription? subscription)
    {
        return (subscription is null) ||
               (subscription.Tier != SubscriptionTier.Team);
    }

    /// <summary>
    /// Whether the tenant must be refused a feature that needs a paid tier of any kind: no
    /// subscription, or the Free tier.
    /// </summary>
    /// <param name="subscription">The tenant's subscription, or <c>null</c> if none exists.</param>
    /// <returns><c>true</c> when access must be denied.</returns>
    /// <remarks>
    /// Distinct from <see cref="RequiresPro"/>, which additionally requires an Active status, and
    /// from <see cref="RequiresTeam"/>, which refuses Pro. This is the rule behind the invitation
    /// upsell, which answers 402 rather than 403.
    /// </remarks>
    public static bool RequiresPaidTier(TenantSubscription? subscription)
    {
        return (subscription is null) ||
               (subscription.Tier == SubscriptionTier.Free);
    }

    /// <summary>
    /// Whether the tenant must be held to read-only access: no subscription, or a canceled one.
    /// </summary>
    /// <param name="subscription">The tenant's subscription, or <c>null</c> if none exists.</param>
    /// <returns><c>true</c> when the mutation must be refused.</returns>
    /// <remarks>
    /// Deliberately not named IsActive. It returns <c>true</c> for a missing subscription as well as
    /// a canceled one, while leaving PastDue free to mutate — an account behind on payment is being
    /// retried, not closed — so an "is active" reading would be wrong in both directions.
    /// </remarks>
    public static bool BlocksMutations(TenantSubscription? subscription)
    {
        return (subscription is null) ||
               (subscription.Status == SubscriptionStatus.Canceled);
    }
}
