// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles billing webhook events.
/// </summary>
public interface IBillingWebhookHandler
{
    /// <summary>
    /// Handles a completed checkout by upgrading the tenant to the specified tier.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="tier">The subscription tier to set.</param>
    /// <param name="ct">A cancellation token.</param>
    Task HandleCheckoutCompletedAsync(int tenantId, SubscriptionTier tier, CancellationToken ct);

    /// <summary>
    /// Handles a subscription update by refreshing the current period end date.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="currentPeriodEnd">The updated period end date.</param>
    /// <param name="ct">A cancellation token.</param>
    Task HandleSubscriptionUpdatedAsync(int tenantId, DateTimeOffset currentPeriodEnd, CancellationToken ct);

    /// <summary>
    /// Handles a subscription deletion by reverting the tenant to the Free tier.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="ct">A cancellation token.</param>
    Task HandleSubscriptionDeletedAsync(int tenantId, CancellationToken ct);

    /// <summary>
    /// Handles a payment failure by marking the subscription as past due.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="ct">A cancellation token.</param>
    Task HandlePaymentFailedAsync(int tenantId, CancellationToken ct);

    /// <summary>
    /// Handles a successful payment by restoring the subscription to active status.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="ct">A cancellation token.</param>
    Task HandlePaymentSucceededAsync(int tenantId, CancellationToken ct);

    /// <summary>
    /// Handles a downgrade from Team to Pro tier.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="ct">A cancellation token.</param>
    Task HandleDowngradeToProAsync(int tenantId, CancellationToken ct);
}
