// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// No-op implementation of IBillingApiClient when billing gRPC is not configured.
/// </summary>
public sealed class NoOpBillingApiClient : IBillingApiClient
{
    /// <inheritdoc/>
    public Task<bool> UpdateQuantityAsync(string tenantExternalId, int machineCount, CancellationToken ct)
    {
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<bool> CancelSubscriptionAsync(string tenantExternalId, CancellationToken ct)
    {
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<StripeSubscriptionStatus> GetSubscriptionStatusAsync(string tenantExternalId, CancellationToken ct)
    {
        return Task.FromResult(new StripeSubscriptionStatus(false, "none", string.Empty, 0, null));
    }

    /// <inheritdoc/>
    public Task<bool> SwapSubscriptionPriceAsync(string tenantExternalId, string targetTier, CancellationToken ct)
    {
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<bool> ResumeSubscriptionAsync(string tenantExternalId, CancellationToken ct)
    {
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<UpcomingInvoiceResult?> GetUpcomingInvoiceAsync(string tenantExternalId, CancellationToken ct)
    {
        return Task.FromResult<UpcomingInvoiceResult?>(null);
    }

    /// <inheritdoc/>
    public Task<List<InvoiceResult>> ListInvoicesAsync(string tenantExternalId, int limit, CancellationToken ct)
    {
        return Task.FromResult<List<InvoiceResult>>([]);
    }
}
