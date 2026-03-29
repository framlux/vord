// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Represents the status of a tenant subscription.
/// </summary>
public enum SubscriptionStatus : int
{
    /// <summary>No subscription status.</summary>
    None = 0,
    /// <summary>Subscription is active.</summary>
    Active = 1,
    /// <summary>Payment is past due.</summary>
    PastDue = 2,
    /// <summary>Subscription has been canceled.</summary>
    Canceled = 3
}
