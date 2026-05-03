// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.Vord.BillingGrpc;

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// gRPC client wrapper for communicating with the billing API's BillingManagement service.
/// All calls include a 10-second deadline to prevent indefinite blocking if the billing API is unresponsive.
/// </summary>
public sealed class BillingApiClient : IBillingApiClient
{
    private static readonly TimeSpan GrpcDeadline = TimeSpan.FromSeconds(10);

    private readonly BillingManagement.BillingManagementClient _grpcClient;
    private readonly ILogger<BillingApiClient> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="BillingApiClient"/> class.
    /// </summary>
    public BillingApiClient(
        BillingManagement.BillingManagementClient grpcClient,
        ILogger<BillingApiClient> logger)
    {
        ArgumentNullException.ThrowIfNull(grpcClient);
        ArgumentNullException.ThrowIfNull(logger);

        _grpcClient = grpcClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<bool> UpdateQuantityAsync(string tenantExternalId, int machineCount, CancellationToken ct)
    {
        try
        {
            UpdateQuantityResponse response = await _grpcClient.UpdateSubscriptionQuantityAsync(
                new UpdateQuantityRequest
                {
                    TenantExternalId = tenantExternalId,
                    MachineCount = machineCount
                },
                deadline: DateTime.UtcNow.Add(GrpcDeadline),
                cancellationToken: ct);

            if (response.Success == false)
            {
                _logger.LogWarning(
                    "Failed to update quantity for tenant {TenantExternalId}: {Message}",
                    tenantExternalId, response.Message);
            }

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error updating quantity for tenant {TenantExternalId}",
                tenantExternalId);

            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ReportMachineUsageAsync(string tenantExternalId, int machineCount, CancellationToken ct)
    {
        try
        {
            ReportMachineUsageResponse response = await _grpcClient.ReportMachineUsageAsync(
                new ReportMachineUsageRequest
                {
                    TenantExternalId = tenantExternalId,
                    MachineCount = machineCount
                },
                deadline: DateTime.UtcNow.Add(GrpcDeadline),
                cancellationToken: ct);

            if (response.Success == false)
            {
                _logger.LogWarning(
                    "Failed to report machine usage for tenant {TenantExternalId}: {Message}",
                    tenantExternalId, response.Message);
            }

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error reporting machine usage for tenant {TenantExternalId}",
                tenantExternalId);

            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CancelSubscriptionAsync(string tenantExternalId, string pendingAction, CancellationToken ct)
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

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error canceling subscription for tenant {TenantExternalId}",
                tenantExternalId);

            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<StripeSubscriptionStatus> GetSubscriptionStatusAsync(
        string tenantExternalId, CancellationToken ct)
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

        return new StripeSubscriptionStatus(
            response.CancelAtPeriodEnd,
            response.StripeStatus,
            response.PriceId,
            response.Quantity,
            currentPeriodEnd);
    }

    /// <inheritdoc/>
    public async Task<bool> SwapSubscriptionPriceAsync(string tenantExternalId, string targetTier, CancellationToken ct)
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

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error swapping subscription price for tenant {TenantExternalId}",
                tenantExternalId);

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

            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error resuming subscription for tenant {TenantExternalId}",
                tenantExternalId);

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
                return null;
            }

            List<InvoiceLineItemResult> lines = response.Lines.Select(l => new InvoiceLineItemResult(
                l.Description,
                l.AmountCents,
                l.Quantity,
                l.PeriodStart?.ToDateTimeOffset(),
                l.PeriodEnd?.ToDateTimeOffset(),
                l.Proration)).ToList();

            return new UpcomingInvoiceResult(
                response.AmountDueCents,
                response.Currency,
                response.PeriodStart?.ToDateTimeOffset(),
                response.PeriodEnd?.ToDateTimeOffset(),
                response.NextPaymentAttempt?.ToDateTimeOffset(),
                response.UnitAmountCents,
                lines);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting upcoming invoice for tenant {TenantExternalId}", tenantExternalId);
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
            return [];
        }
    }
}
