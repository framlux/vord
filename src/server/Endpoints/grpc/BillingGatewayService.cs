// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Handlers;
using Framlux.Vord.BillingGrpc;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Endpoints.Grpc;

/// <summary>
/// gRPC service for processing billing actions dispatched from the billing API.
/// Replaces the legacy REST BillingGatewayEndpoint.
/// </summary>
public sealed class BillingGatewayService : BillingGateway.BillingGatewayBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly InternalApiOptions _internalApiOptions;
    private readonly ILogger<BillingGatewayService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="BillingGatewayService"/> class.
    /// </summary>
    public BillingGatewayService(
        IServiceScopeFactory scopeFactory,
        IOptions<InternalApiOptions> internalApiOptions,
        ILogger<BillingGatewayService> logger)
    {
        _scopeFactory = scopeFactory;
        _internalApiOptions = internalApiOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Processes a billing action from the billing API via gRPC.
    /// </summary>
    public override async Task<BillingActionResponse> ProcessBillingAction(
        BillingActionRequest request, ServerCallContext context)
    {
        ValidateInternalKey(context);

        using IServiceScope scope = _scopeFactory.CreateScope();
        IDatabaseCache databaseCache = scope.ServiceProvider.GetRequiredService<IDatabaseCache>();
        IBillingWebhookHandler webhookHandler = scope.ServiceProvider.GetRequiredService<IBillingWebhookHandler>();

        Tenant? tenant = await databaseCache.GetTenantByExternalIdAsync(
            request.TenantExternalId, context.CancellationToken);

        if (tenant is null)
        {
            throw new RpcException(new Status(
                StatusCode.NotFound,
                $"Tenant not found for external ID: {request.TenantExternalId}"));
        }

        switch (request.Action)
        {
            case BillingAction.UpgradeToPro:
                await webhookHandler.HandleCheckoutCompletedAsync(tenant.Id, SubscriptionTier.Pro, context.CancellationToken);
                _logger.LogInformation("Billing gRPC: tenant {TenantId} upgraded to Pro", tenant.Id);

                break;

            case BillingAction.UpgradeToTeam:
                await webhookHandler.HandleCheckoutCompletedAsync(tenant.Id, SubscriptionTier.Team, context.CancellationToken);
                _logger.LogInformation("Billing gRPC: tenant {TenantId} upgraded to Team", tenant.Id);

                break;

            case BillingAction.DowngradeToFree:
                await webhookHandler.HandleSubscriptionDeletedAsync(tenant.Id, context.CancellationToken);
                _logger.LogInformation("Billing gRPC: tenant {TenantId} downgraded to Free", tenant.Id);

                break;

            case BillingAction.DowngradeToPro:
                await webhookHandler.HandleDowngradeToProAsync(tenant.Id, context.CancellationToken);
                _logger.LogInformation("Billing gRPC: tenant {TenantId} downgraded to Pro", tenant.Id);

                break;

            case BillingAction.UpdatePeriodEnd:
                if (request.CurrentPeriodEnd is null)
                {
                    throw new RpcException(new Status(
                        StatusCode.InvalidArgument,
                        "CurrentPeriodEnd is required for UPDATE_PERIOD_END action"));
                }

                DateTimeOffset periodEnd = request.CurrentPeriodEnd.ToDateTimeOffset();
                await webhookHandler.HandleSubscriptionUpdatedAsync(tenant.Id, periodEnd, context.CancellationToken);
                _logger.LogInformation("Billing gRPC: tenant {TenantId} period end updated", tenant.Id);

                break;

            case BillingAction.SetPastDue:
                await webhookHandler.HandlePaymentFailedAsync(tenant.Id, context.CancellationToken);
                _logger.LogInformation("Billing gRPC: tenant {TenantId} marked past due", tenant.Id);

                break;

            case BillingAction.SetActive:
                await webhookHandler.HandlePaymentSucceededAsync(tenant.Id, context.CancellationToken);
                _logger.LogInformation("Billing gRPC: tenant {TenantId} set to active", tenant.Id);

                break;

            default:
                throw new RpcException(new Status(
                    StatusCode.InvalidArgument,
                    $"Unknown billing action: {request.Action}"));
        }

        return new BillingActionResponse
        {
            Success = true,
            Message = "OK"
        };
    }

    private void ValidateInternalKey(ServerCallContext context)
    {
        string configuredKey = _internalApiOptions.Key;
        if (string.IsNullOrEmpty(configuredKey))
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "Internal API is not configured"));
        }

        Metadata.Entry? keyEntry = context.RequestHeaders.Get("x-internal-key");
        string providedKey = keyEntry?.Value ?? string.Empty;

        if (string.Equals(providedKey, configuredKey, StringComparison.Ordinal) == false)
        {
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Unauthorized"));
        }
    }
}
