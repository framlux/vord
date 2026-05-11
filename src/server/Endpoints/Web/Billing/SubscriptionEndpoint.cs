// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Server.Services.Billing;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Billing;

/// <summary>
/// Subscription information returned to the UI.
/// </summary>
public sealed class SubscriptionDto
{
    /// <summary>The subscription tier.</summary>
    public string Tier { get; set; } = string.Empty;

    /// <summary>The subscription status.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Maximum machines allowed.</summary>
    public int MachineLimit { get; set; }

    /// <summary>Current active machine count.</summary>
    public int MachineCount { get; set; }

    /// <summary>Data retention in days.</summary>
    public int RetentionDays { get; set; }

    /// <summary>End of current billing period.</summary>
    public DateTimeOffset? CurrentPeriodEnd { get; set; }

    /// <summary>Whether the subscription is set to cancel at the end of the billing period.</summary>
    public bool CancelAtPeriodEnd { get; set; }

    /// <summary>Maximum alert rules allowed.</summary>
    public int AlertRuleLimit { get; set; }

    /// <summary>Current alert rule count for this tenant.</summary>
    public int AlertRuleCount { get; set; }

    /// <summary>Maximum webhooks allowed.</summary>
    public int WebhookLimit { get; set; }

    /// <summary>Current webhook count for this tenant.</summary>
    public int WebhookCount { get; set; }
}

/// <summary>
/// Returns the current tenant's subscription information.
/// </summary>
public sealed class SubscriptionEndpoint : EndpointWithoutRequest<ApiResponse<SubscriptionDto>>
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IIntegrationRepository _integrationRepo;
    private readonly ITenantRepository _tenantRepository;
    private readonly IBillingApiClient _billingApiClient;

    /// <summary>
    /// Creates a new instance of the <see cref="SubscriptionEndpoint"/> class.
    /// </summary>
    public SubscriptionEndpoint(
        ISubscriptionService subscriptionService,
        IAlertRuleRepository alertRuleRepo,
        IIntegrationRepository integrationRepo,
        ITenantRepository tenantRepository,
        IBillingApiClient billingApiClient)
    {
        _subscriptionService = subscriptionService;
        _alertRuleRepo = alertRuleRepo;
        _integrationRepo = integrationRepo;
        _tenantRepository = tenantRepository;
        _billingApiClient = billingApiClient;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/billing/subscription");
        Policies("ViewOnly");
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int? tenantId = TenantClaimHelper.GetTenantIdFromClaims(User, HttpContext);
        if (tenantId is null)
        {
            HttpContext.Response.StatusCode = 401;
            await HttpContext.Response.WriteAsJsonAsync(ApiResponse<SubscriptionDto>.Error("Unauthorized"), ct);

            return;
        }

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);
        if (subscription is null)
        {
            HttpContext.Response.StatusCode = 404;
            await HttpContext.Response.WriteAsJsonAsync(
                ApiResponse<SubscriptionDto>.Error("Subscription not found"), ct);

            return;
        }

        int machineCount = await _subscriptionService.GetMachineCountForTenantAsync(tenantId.Value, ct);
        int alertRuleCount = await _alertRuleRepo.CountAlertRulesForTenantAsync(tenantId.Value, ct);
        int webhookCount = await _integrationRepo.CountIntegrationsForTenantAsync(tenantId.Value, ct);
        EffectiveLimits limits = await _subscriptionService.GetEffectiveLimitsForTenantAsync(tenantId.Value, ct);

        // Retrieve cancellation state from billing-api (source of truth for Stripe state)
        bool cancelAtPeriodEnd = false;
        if (subscription.Tier != SubscriptionTier.Free)
        {
            Tenant? tenant = await _tenantRepository.GetTenantByIdAsync(tenantId.Value, ct);
            if (tenant is not null)
            {
                StripeSubscriptionStatus stripeStatus = await _billingApiClient.GetSubscriptionStatusAsync(tenant.ExternalId, ct);
                cancelAtPeriodEnd = stripeStatus.CancelAtPeriodEnd;
            }
        }

        SubscriptionDto dto = new()
        {
            Tier = subscription.Tier.ToString(),
            Status = subscription.Status.ToString(),
            MachineLimit = limits.MachineLimit,
            MachineCount = machineCount,
            RetentionDays = limits.RetentionDays,
            CurrentPeriodEnd = subscription.CurrentPeriodEnd,
            CancelAtPeriodEnd = cancelAtPeriodEnd,
            AlertRuleLimit = limits.AlertRuleLimit,
            AlertRuleCount = alertRuleCount,
            WebhookLimit = limits.WebhookLimit,
            WebhookCount = webhookCount,
        };

        await Send.OkAsync(ApiResponse<SubscriptionDto>.Ok(dto), cancellation: ct);
    }
}
