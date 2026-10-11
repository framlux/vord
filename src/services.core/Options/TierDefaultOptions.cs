// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Configuration-driven default limits for each subscription tier.
/// The database TierFeatureLimits table is the live source of truth (editable via admin panel).
/// These defaults serve as the initial seed values and a resilient fallback if the database
/// lookup fails or the tier row is missing.
/// </summary>
public sealed class TierDefaultOptions
{
    /// <summary>
    /// Default limits for the Free tier.
    /// </summary>
    public TierLimitDefaults Free { get; set; } = new();

    /// <summary>
    /// Default limits for the Pro tier.
    /// </summary>
    public TierLimitDefaults Pro { get; set; } = new();

    /// <summary>
    /// Default limits for the Team tier.
    /// </summary>
    public TierLimitDefaults Team { get; set; } = new();

    /// <summary>
    /// Fallback limits for the Enterprise tier. An Enterprise tenant's limits come from its
    /// agreement; these apply only to settings agreements do not set, such as the export cooldown.
    /// </summary>
    public TierLimitDefaults Enterprise { get; set; } = new();

    /// <summary>
    /// The configured defaults for a tier. An unknown tier resolves to Free, the same way a missing
    /// subscription does, so an unrecognised value can never widen a limit.
    /// </summary>
    /// <param name="tier">The tier whose defaults to return.</param>
    /// <returns>The defaults for <paramref name="tier"/>, or the Free defaults when the tier is not recognised.</returns>
    public TierLimitDefaults For(SubscriptionTier tier)
    {
        return tier switch
        {
            SubscriptionTier.Pro => Pro,
            SubscriptionTier.Team => Team,
            SubscriptionTier.Enterprise => Enterprise,
            _ => Free,
        };
    }
}
