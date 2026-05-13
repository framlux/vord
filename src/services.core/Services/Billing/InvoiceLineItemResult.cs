// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// A line item on an upcoming invoice.
/// </summary>
public sealed record InvoiceLineItemResult(
    string Description,
    long AmountCents,
    long Quantity,
    DateTimeOffset? PeriodStart,
    DateTimeOffset? PeriodEnd,
    bool Proration);
