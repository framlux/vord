// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Options;

/// <summary>
/// Configuration options for Stripe billing integration.
/// </summary>
public sealed class BillingOptions
{
    /// <summary>
    /// Whether billing is enabled for this deployment.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The gRPC URL for the billing API service.
    /// </summary>
    public string GrpcUrl { get; set; } = string.Empty;

    /// <summary>
    /// The Stripe price ID for the Pro tier.
    /// </summary>
    public string StripeProPriceId { get; set; } = string.Empty;

    /// <summary>
    /// The Stripe price ID for the Team tier.
    /// </summary>
    public string StripeTeamPriceId { get; set; } = string.Empty;

    /// <summary>
    /// The Stripe price ID for the Pro tier monthly metered subscription.
    /// </summary>
    public string StripeProMonthlyPriceId { get; set; } = string.Empty;

    /// <summary>
    /// The Stripe price ID for the Pro tier annual metered subscription.
    /// </summary>
    public string StripeProAnnualPriceId { get; set; } = string.Empty;

    /// <summary>
    /// The Stripe price ID for the Team tier monthly metered subscription.
    /// </summary>
    public string StripeTeamMonthlyPriceId { get; set; } = string.Empty;

    /// <summary>
    /// The Stripe price ID for the Team tier annual metered subscription.
    /// </summary>
    public string StripeTeamAnnualPriceId { get; set; } = string.Empty;
}
