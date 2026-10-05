// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Represents the subscription tier for a tenant.
/// </summary>
public enum SubscriptionTier : int
{
    /// <summary>No subscription.</summary>
    None = 0,
    /// <summary>Free tier with limited hosts.</summary>
    Free = 1,
    /// <summary>Pro tier with unlimited hosts and per-host billing.</summary>
    Pro = 2,
    /// <summary>Team tier with custom OIDC and advanced features.</summary>
    Team = 3,
    /// <summary>
    /// Every Team feature, with limits set per enterprise agreement and billing by invoice outside
    /// Stripe. Entered only through an applied agreement; no Stripe path may move a tenant into or out of it.
    /// </summary>
    Enterprise = 4
}
