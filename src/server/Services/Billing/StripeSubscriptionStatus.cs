// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// Represents the current subscription status retrieved from Stripe via the billing gRPC service.
/// </summary>
/// <param name="CancelAtPeriodEnd">Whether the subscription is set to cancel at the end of the current period.</param>
/// <param name="StripeStatus">The Stripe subscription status string (e.g., "active", "past_due", "canceled", "none").</param>
/// <param name="PriceId">The Stripe price ID of the current subscription item.</param>
/// <param name="Quantity">The quantity on the current subscription item.</param>
/// <param name="CurrentPeriodEnd">The end of the current billing period, if available.</param>
public sealed record StripeSubscriptionStatus(
    bool CancelAtPeriodEnd,
    string StripeStatus,
    string PriceId,
    int Quantity,
    DateTimeOffset? CurrentPeriodEnd);
