// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.Vord.BillingGrpc;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// gRPC client wrapper for communicating with the billing API's BillingManagement service.
/// All calls include a 10-second deadline to prevent indefinite blocking if the billing API is unresponsive.
/// </summary>
public sealed class BillingApiClient : IBillingApiClient
{
    private static readonly TimeSpan GrpcDeadline = TimeSpan.FromSeconds(10);

    private readonly BillingManagement.BillingManagementClient _grpcClient;
    private readonly BillingMetrics _billingMetrics;
    private readonly ILogger<BillingApiClient> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="BillingApiClient"/> class.
    /// </summary>
    public BillingApiClient(
        BillingManagement.BillingManagementClient grpcClient,
        BillingMetrics billingMetrics,
        ILogger<BillingApiClient> logger)
    {
        ArgumentNullException.ThrowIfNull(grpcClient);
        ArgumentNullException.ThrowIfNull(billingMetrics);
        ArgumentNullException.ThrowIfNull(logger);

        _grpcClient = grpcClient;
        _billingMetrics = billingMetrics;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateQuantityAsync(string tenantExternalId, int quantity, CancellationToken ct)
    {
        try
        {
            UpdateQuantityResponse response = await _grpcClient.UpdateSubscriptionQuantityAsync(
                new UpdateQuantityRequest
                {
                    TenantExternalId = tenantExternalId,
                    Quantity = quantity,
                }, deadline: DateTime.UtcNow.Add(GrpcDeadline), cancellationToken: ct);

            if (response.Success == false)
            {
                _logger.LogWarning(
                    "Failed to update quantity for tenant {TenantExternalId}: {Message}",
                    tenantExternalId, response.Message);
            }

            _billingMetrics.RecordOperation(
                BillingOperation.UpdateQuantity,
                response.Success ? BillingOperationOutcome.Ok : BillingOperationOutcome.Failed);

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error updating quantity for tenant {TenantExternalId}",
                tenantExternalId);

            _billingMetrics.RecordOperation(BillingOperation.UpdateQuantity, BillingOperationOutcome.Error);

            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CancelSubscriptionAsync(string tenantExternalId, PendingActionType pendingAction, CancellationToken ct)
    {
        try
        {
            CancelSubscriptionResponse response = await _grpcClient.CancelSubscriptionAsync(
                new CancelSubscriptionRequest
                {
                    TenantExternalId = tenantExternalId,
                    PendingAction = pendingAction
                },
                deadline: DateTime.UtcNow.Add(GrpcDeadline),
                cancellationToken: ct);

            if (response.Success == false)
            {
                _logger.LogWarning(
                    "Failed to cancel subscription for tenant {TenantExternalId}: {Message}",
                    tenantExternalId, response.Message);
            }

            _billingMetrics.RecordOperation(
                BillingOperation.CancelSubscription,
                response.Success ? BillingOperationOutcome.Ok : BillingOperationOutcome.Failed);

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error canceling subscription for tenant {TenantExternalId}",
                tenantExternalId);

            _billingMetrics.RecordOperation(BillingOperation.CancelSubscription, BillingOperationOutcome.Error);

            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CancelSubscriptionImmediateAsync(string tenantExternalId, CancellationToken ct)
    {
        try
        {
            CancelSubscriptionImmediateResponse response = await _grpcClient.CancelSubscriptionImmediateAsync(
                new CancelSubscriptionImmediateRequest { TenantExternalId = tenantExternalId },
                deadline: DateTime.UtcNow.Add(GrpcDeadline),
                cancellationToken: ct);

            if (response.Success == false)
            {
                _logger.LogWarning(
                    "Failed to immediately cancel subscription for tenant {TenantExternalId}: {Message}",
                    tenantExternalId, response.Message);
            }

            _billingMetrics.RecordOperation(
                BillingOperation.CancelSubscriptionImmediate,
                response.Success ? BillingOperationOutcome.Ok : BillingOperationOutcome.Failed);

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error immediately canceling subscription for tenant {TenantExternalId}", tenantExternalId);

            _billingMetrics.RecordOperation(BillingOperation.CancelSubscriptionImmediate, BillingOperationOutcome.Error);

            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteCustomerAsync(string tenantExternalId, CancellationToken ct)
    {
        try
        {
            DeleteCustomerResponse response = await _grpcClient.DeleteCustomerAsync(
                new DeleteCustomerRequest { TenantExternalId = tenantExternalId },
                deadline: DateTime.UtcNow.Add(GrpcDeadline),
                cancellationToken: ct);

            if (response.Success == false)
            {
                _logger.LogWarning(
                    "Failed to delete billing customer for tenant {TenantExternalId}: {Message}",
                    tenantExternalId, response.Message);
            }

            _billingMetrics.RecordOperation(
                BillingOperation.DeleteCustomer,
                response.Success ? BillingOperationOutcome.Ok : BillingOperationOutcome.Failed);

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting billing customer for tenant {TenantExternalId}", tenantExternalId);

            _billingMetrics.RecordOperation(BillingOperation.DeleteCustomer, BillingOperationOutcome.Error);

            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<StripeSubscriptionStatus> GetSubscriptionStatusAsync(
        string tenantExternalId, CancellationToken ct)
    {
        try
        {
            GetSubscriptionStatusResponse response = await _grpcClient.GetSubscriptionStatusAsync(
                new GetSubscriptionStatusRequest
                {
                    TenantExternalId = tenantExternalId
                },
                deadline: DateTime.UtcNow.Add(GrpcDeadline),
                cancellationToken: ct);

            DateTimeOffset? currentPeriodEnd = response.CurrentPeriodEnd is not null
                ? response.CurrentPeriodEnd.ToDateTimeOffset()
                : null;

            _billingMetrics.RecordOperation(BillingOperation.GetSubscriptionStatus, BillingOperationOutcome.Ok);

            return new StripeSubscriptionStatus(
                response.CancelAtPeriodEnd,
                response.StripeStatus,
                response.Quantity,
                currentPeriodEnd,
                response.Tier,
                response.BillingInterval);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error getting subscription status for tenant {TenantExternalId}",
                tenantExternalId);

            _billingMetrics.RecordOperation(BillingOperation.GetSubscriptionStatus, BillingOperationOutcome.Error);

            return new StripeSubscriptionStatus(false, "none", 0, null, BillingTier.Unspecified, BillingInterval.None);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SwapSubscriptionPriceAsync(string tenantExternalId, BillingTier targetTier, CancellationToken ct)
    {
        try
        {
            SwapSubscriptionPriceResponse response = await _grpcClient.SwapSubscriptionPriceAsync(
                new SwapSubscriptionPriceRequest
                {
                    TenantExternalId = tenantExternalId,
                    TargetTier = targetTier
                },
                deadline: DateTime.UtcNow.Add(GrpcDeadline),
                cancellationToken: ct);

            if (response.Success == false)
            {
                _logger.LogWarning(
                    "Failed to swap subscription price for tenant {TenantExternalId}: {Message}",
                    tenantExternalId, response.Message);
            }

            _billingMetrics.RecordOperation(
                BillingOperation.SwapSubscriptionPrice,
                response.Success ? BillingOperationOutcome.Ok : BillingOperationOutcome.Failed);

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error swapping subscription price for tenant {TenantExternalId}",
                tenantExternalId);

            _billingMetrics.RecordOperation(BillingOperation.SwapSubscriptionPrice, BillingOperationOutcome.Error);

            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ResumeSubscriptionAsync(string tenantExternalId, CancellationToken ct)
    {
        try
        {
            ResumeSubscriptionResponse response = await _grpcClient.ResumeSubscriptionAsync(
                new ResumeSubscriptionRequest
                {
                    TenantExternalId = tenantExternalId
                },
                deadline: DateTime.UtcNow.Add(GrpcDeadline),
                cancellationToken: ct);

            if (response.Success == false)
            {
                _logger.LogWarning(
                    "Failed to resume subscription for tenant {TenantExternalId}: {Message}",
                    tenantExternalId, response.Message);
            }

            _billingMetrics.RecordOperation(
                BillingOperation.ResumeSubscription,
                response.Success ? BillingOperationOutcome.Ok : BillingOperationOutcome.Failed);

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error resuming subscription for tenant {TenantExternalId}",
                tenantExternalId);

            _billingMetrics.RecordOperation(BillingOperation.ResumeSubscription, BillingOperationOutcome.Error);

            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<UpcomingInvoiceResult?> GetUpcomingInvoiceAsync(string tenantExternalId, CancellationToken ct)
    {
        try
        {
            GetUpcomingInvoiceResponse response = await _grpcClient.GetUpcomingInvoiceAsync(
                new GetUpcomingInvoiceRequest { TenantExternalId = tenantExternalId },
                deadline: DateTime.UtcNow.Add(GrpcDeadline),
                cancellationToken: ct);

            if (response.HasInvoice == false)
            {
                // A normal answer, not a failure: there is simply no invoice to preview.
                _billingMetrics.RecordOperation(BillingOperation.GetUpcomingInvoice, BillingOperationOutcome.Ok);

                return null;
            }

            List<InvoiceLineItemResult> lines = response.Lines.Select(l => new InvoiceLineItemResult(
                l.Description,
                l.AmountCents,
                l.Quantity,
                l.PeriodStart?.ToDateTimeOffset(),
                l.PeriodEnd?.ToDateTimeOffset(),
                l.Proration)).ToList();

            // Sum negative line items to determine discount amount
            long discountAmountCents = Math.Abs(lines
                .Where(l => l.AmountCents < 0)
                .Sum(l => l.AmountCents));

            _billingMetrics.RecordOperation(BillingOperation.GetUpcomingInvoice, BillingOperationOutcome.Ok);

            return new UpcomingInvoiceResult(
                response.AmountDueCents,
                response.Currency,
                response.PeriodStart?.ToDateTimeOffset(),
                response.PeriodEnd?.ToDateTimeOffset(),
                response.NextPaymentAttempt?.ToDateTimeOffset(),
                response.UnitAmountCents,
                discountAmountCents,
                lines);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting upcoming invoice for tenant {TenantExternalId}", tenantExternalId);
            _billingMetrics.RecordOperation(BillingOperation.GetUpcomingInvoice, BillingOperationOutcome.Error);

            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<List<InvoiceResult>> ListInvoicesAsync(string tenantExternalId, int limit, CancellationToken ct)
    {
        try
        {
            ListInvoicesResponse response = await _grpcClient.ListInvoicesAsync(
                new ListInvoicesRequest { TenantExternalId = tenantExternalId, Limit = limit },
                deadline: DateTime.UtcNow.Add(GrpcDeadline),
                cancellationToken: ct);

            _billingMetrics.RecordOperation(BillingOperation.ListInvoices, BillingOperationOutcome.Ok);

            return response.Invoices.Select(inv => new InvoiceResult(
                inv.Id,
                inv.AmountCents,
                inv.Currency,
                inv.Status,
                inv.Created?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                inv.PeriodStart?.ToDateTimeOffset(),
                inv.PeriodEnd?.ToDateTimeOffset(),
                inv.HostedInvoiceUrl,
                inv.InvoicePdfUrl)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing invoices for tenant {TenantExternalId}", tenantExternalId);
            _billingMetrics.RecordOperation(BillingOperation.ListInvoices, BillingOperationOutcome.Error);

            return [];
        }
    }

    /// <inheritdoc/>
    public async Task<List<CatalogItemResult>> GetPublicCatalogAsync(CancellationToken ct)
    {
        try
        {
            GetPublicCatalogResponse response = await _grpcClient.GetPublicCatalogAsync(
                new GetPublicCatalogRequest(),
                deadline: DateTime.UtcNow.Add(GrpcDeadline),
                cancellationToken: ct);

            _billingMetrics.RecordOperation(BillingOperation.GetPublicCatalog, BillingOperationOutcome.Ok);

            return response.Items.Select(i => new CatalogItemResult(
                i.Tier,
                i.Interval,
                i.UnitAmountCents,
                i.Currency)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting public billing catalog");
            _billingMetrics.RecordOperation(BillingOperation.GetPublicCatalog, BillingOperationOutcome.Error);

            return [];
        }
    }
}
