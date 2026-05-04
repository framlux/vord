// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// Constants for pending actions passed to the billing-api during subscription
/// cancellation or downgrade. These values are stored in the billing database
/// and referenced by the webhook processor to determine post-cancellation behavior.
/// </summary>
public static class PendingActions
{
    /// <summary>Indicates the tenant intends to fully cancel their account.</summary>
    public const string CancelAccount = "CancelAccount";

    /// <summary>Indicates the tenant intends to downgrade to the Free tier at period end.</summary>
    public const string DowngradeToFree = "DowngradeToFree";

    /// <summary>Indicates the tenant intends to downgrade to the Pro tier at period end.</summary>
    public const string DowngradeToPro = "DowngradeToPro";
}
