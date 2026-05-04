// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// Client for communicating with the billing API via gRPC.
/// </summary>
public interface IBillingApiClient
{
    /// <summary>
    /// Updates the subscription quantity in Stripe to match the current machine count.
    /// </summary>
    /// <param name="tenantExternalId">The tenant external ID.</param>
    /// <param name="machineCount">The current active machine count.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the update was successful.</returns>
    Task<bool> UpdateQuantityAsync(string tenantExternalId, int machineCount, CancellationToken ct);

    /// <summary>
    /// Reports the current machine count for a tenant to the billing API for metered billing.
    /// </summary>
    /// <param name="tenantExternalId">The tenant external ID.</param>
    /// <param name="machineCount">The current active machine count.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the usage report was successful.</returns>
    Task<bool> ReportMachineUsageAsync(string tenantExternalId, int machineCount, CancellationToken ct);

    /// <summary>
    /// Cancels a subscription at the end of the current billing period.
    /// </summary>
    /// <param name="tenantExternalId">The tenant external ID.</param>
    /// <param name="pendingAction">The action to take when the subscription ends (e.g., "DowngradeToFree", "DowngradeToPro", "CancelAccount").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the cancellation was successful.</returns>
    Task<bool> CancelSubscriptionAsync(string tenantExternalId, string pendingAction, CancellationToken ct);

    /// <summary>
    /// Gets the current subscription status from Stripe for reconciliation.
    /// </summary>
    /// <param name="tenantExternalId">The tenant external ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current Stripe subscription status.</returns>
    Task<StripeSubscriptionStatus> GetSubscriptionStatusAsync(string tenantExternalId, CancellationToken ct);

    /// <summary>
    /// Swaps the subscription price in Stripe for an immediate tier change with proration.
    /// </summary>
    /// <param name="tenantExternalId">The tenant external ID.</param>
    /// <param name="targetTier">The target tier to swap to (e.g., "pro").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the swap was successful.</returns>
    Task<bool> SwapSubscriptionPriceAsync(string tenantExternalId, string targetTier, CancellationToken ct);

    /// <summary>
    /// Resumes a subscription that was set to cancel at period end.
    /// </summary>
    /// <param name="tenantExternalId">The tenant external ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the resume was successful.</returns>
    Task<bool> ResumeSubscriptionAsync(string tenantExternalId, CancellationToken ct);

    /// <summary>
    /// Gets the upcoming invoice with prorated charges for the current billing period.
    /// </summary>
    Task<UpcomingInvoiceResult?> GetUpcomingInvoiceAsync(string tenantExternalId, CancellationToken ct);

    /// <summary>
    /// Lists recent paid invoices.
    /// </summary>
    Task<List<InvoiceResult>> ListInvoicesAsync(string tenantExternalId, int limit, CancellationToken ct);
}

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
