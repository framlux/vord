// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Which control-plane call was made. One member per public method on the billing client, so a new
/// method without a member here is a deliberate addition rather than a call recorded under a
/// neighbour's name.
/// </summary>
public enum BillingOperation
{
    /// <summary>Update the seat quantity on a subscription.</summary>
    UpdateQuantity,

    /// <summary>Schedule a subscription cancellation.</summary>
    CancelSubscription,

    /// <summary>Cancel a subscription immediately.</summary>
    CancelSubscriptionImmediate,

    /// <summary>Delete the billing customer record.</summary>
    DeleteCustomer,

    /// <summary>Read current subscription status.</summary>
    GetSubscriptionStatus,

    /// <summary>Move a subscription to a different tier's price.</summary>
    SwapSubscriptionPrice,

    /// <summary>Undo a scheduled cancellation.</summary>
    ResumeSubscription,

    /// <summary>Read the next invoice preview.</summary>
    GetUpcomingInvoice,

    /// <summary>List past invoices.</summary>
    ListInvoices,

    /// <summary>Read the public price catalog.</summary>
    GetPublicCatalog,
}
