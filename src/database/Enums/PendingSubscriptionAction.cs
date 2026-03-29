// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Represents the action that will occur when a subscription's current billing period ends.
/// </summary>
public enum PendingSubscriptionAction : short
{
    /// <summary>No pending action.</summary>
    None = 0,
    /// <summary>Downgrade the subscription to the Free tier at period end.</summary>
    DowngradeToFree = 1,
    /// <summary>Cancel the account entirely at period end.</summary>
    CancelAccount = 2,
    /// <summary>Downgrade the subscription to the Pro tier at period end.</summary>
    DowngradeToPro = 3,
}
