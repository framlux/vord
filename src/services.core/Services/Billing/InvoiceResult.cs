// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// A past invoice from Stripe.
/// </summary>
public sealed record InvoiceResult(
    string Id,
    long AmountCents,
    string Currency,
    string Status,
    DateTimeOffset Created,
    DateTimeOffset? PeriodStart,
    DateTimeOffset? PeriodEnd,
    string HostedInvoiceUrl,
    string InvoicePdfUrl);
