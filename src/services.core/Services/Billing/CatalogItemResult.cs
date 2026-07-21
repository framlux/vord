// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.Vord.BillingGrpc;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// A single public catalog price entry from the billing API.
/// </summary>
/// <param name="Tier">The billing tier this price applies to.</param>
/// <param name="Interval">The billing recurrence interval.</param>
/// <param name="UnitAmountCents">Per-machine price in cents.</param>
/// <param name="Currency">Three-letter ISO currency code.</param>
/// <param name="IsMetered">Whether the price is metered (billed on reported usage).</param>
public sealed record CatalogItemResult(
    BillingTier Tier,
    BillingInterval Interval,
    long UnitAmountCents,
    string Currency,
    bool IsMetered);
