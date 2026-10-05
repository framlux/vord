// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FastEndpoints;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Auth;
using Framlux.FleetManagement.Services.Core.Billing;

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

    /// <summary>Billing interval of the live subscription ("monthly" or "annual"), or null when not applicable.</summary>
    public string? BillingInterval { get; set; }

    /// <summary>Maximum alert rules allowed.</summary>
    public int AlertRuleLimit { get; set; }

    /// <summary>Current alert rule count for this tenant.</summary>
    public int AlertRuleCount { get; set; }

    /// <summary>Maximum webhooks allowed.</summary>
    public int WebhookLimit { get; set; }

    /// <summary>Current webhook count for this tenant.</summary>
    public int WebhookCount { get; set; }

    /// <summary>The member limit in effect: the agreement's for Enterprise, otherwise the tier's.</summary>
    public int MemberLimit { get; set; }

    /// <summary>Active members plus pending invitations, the count the member limit is enforced against.</summary>
    public int MemberCount { get; set; }
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
    private readonly IInvitationRepository _invitationRepository;
    private readonly ITenantContext _tenantContext;
    private readonly IBillingApiClient _billingApiClient;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a new instance of the <see cref="SubscriptionEndpoint"/> class.
    /// </summary>
    public SubscriptionEndpoint(
        ISubscriptionService subscriptionService,
        IAlertRuleRepository alertRuleRepo,
        IIntegrationRepository integrationRepo,
        ITenantRepository tenantRepository,
        IInvitationRepository invitationRepository,
        ITenantContext tenantContext,
        IBillingApiClient billingApiClient,
        TimeProvider timeProvider)
    {
        _subscriptionService = subscriptionService;
        _alertRuleRepo = alertRuleRepo;
        _integrationRepo = integrationRepo;
        _tenantRepository = tenantRepository;
        _invitationRepository = invitationRepository;
        _tenantContext = tenantContext;
        _billingApiClient = billingApiClient;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public override void Configure()
    {
        Get("/billing/subscription");
        Policies(AuthorizationPolicies.ViewOnly);
        Tags(EndpointTags.RequiresTenant);
        Version(1);
    }

    /// <inheritdoc/>
    public override async Task HandleAsync(CancellationToken ct)
    {
        int tenantId = _tenantContext.RequireTenantId();

        TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId, ct);
        if (subscription is null)
        {
            await HttpContext.SendApiErrorAsync(404, "Subscription not found", ct);

            return;
        }

        int machineCount = await _subscriptionService.GetMachineCountForTenantAsync(tenantId, ct);
        // Matches the entitlement check: the reported usage is authored rules only, so the figure
        // shown to the tenant is the same one that decides whether another rule may be created.
        int alertRuleCount = await _alertRuleRepo.CountCustomAlertRulesForTenantAsync(tenantId, ct);
        int webhookCount = await _integrationRepo.CountIntegrationsForTenantAsync(tenantId, ct);
        EffectiveLimits limits = await _subscriptionService.GetEffectiveLimitsForTenantAsync(tenantId, ct);

        // Pending invitations hold a seat, so the usage shown is the same count the member limit is enforced against.
        int activeMembers = await _tenantRepository.CountActiveMembersAsync(tenantId, ct);
        int pendingInvitations = await _invitationRepository.CountPendingInvitationsAsync(tenantId, _timeProvider.GetUtcNow(), ct);

        // Retrieve cancellation state and billing interval from billing-api (source of truth for Stripe state)
        bool cancelAtPeriodEnd = false;
        string? billingInterval = null;
        // Only Stripe-billed tiers have a Stripe subscription to ask about. An Enterprise tenant is
        // invoiced outside Stripe, and asking would log a NotFound on every page load.
        if (StripeBilledTiers.Contains(subscription.Tier))
        {
            Tenant? tenant = await _tenantRepository.GetTenantByIdAsync(tenantId, ct);
            if (tenant is not null)
            {
                StripeSubscriptionStatus stripeStatus = await _billingApiClient.GetSubscriptionStatusAsync(tenant.ExternalId, ct);
                cancelAtPeriodEnd = stripeStatus.CancelAtPeriodEnd;
                billingInterval = BillingIntervalFormat.ToWireString(stripeStatus.Interval);
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
            BillingInterval = billingInterval,
            AlertRuleLimit = limits.AlertRuleLimit,
            AlertRuleCount = alertRuleCount,
            WebhookLimit = limits.WebhookLimit,
            WebhookCount = webhookCount,
            MemberLimit = limits.MemberLimit,
            MemberCount = activeMembers + pendingInvitations,
        };

        await Send.OkAsync(ApiResponse<SubscriptionDto>.Ok(dto), cancellation: ct);
    }
}
