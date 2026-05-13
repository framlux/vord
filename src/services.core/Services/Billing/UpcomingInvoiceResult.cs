// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Upcoming invoice data from Stripe.
/// </summary>
public sealed record UpcomingInvoiceResult(
    long AmountDueCents,
    string Currency,
    DateTimeOffset? PeriodStart,
    DateTimeOffset? PeriodEnd,
    DateTimeOffset? NextPaymentAttempt,
    long UnitAmountCents,
    long DiscountAmountCents,
    List<InvoiceLineItemResult> Lines);
