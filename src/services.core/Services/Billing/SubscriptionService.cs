// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Service for managing tenant subscriptions and billing.
/// </summary>
public sealed class SubscriptionService : ISubscriptionService
{
    private readonly ISubscriptionRepository _subscriptionRepo;
    private readonly IMachineRepository _machineRepo;
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IIntegrationRepository _integrationRepo;
    private readonly ITierFeatureLimitRepository _tierLimitRepo;
    private readonly ITenantSubscriptionOverrideRepository _overrideRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IInvitationRepository _invitationRepo;
    private readonly TierDefaultOptions _tierDefaults;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubscriptionService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="SubscriptionService"/> class.
    /// </summary>
    public SubscriptionService(
        ISubscriptionRepository subscriptionRepo,
        IMachineRepository machineRepo,
        IAlertRuleRepository alertRuleRepo,
        IIntegrationRepository integrationRepo,
        ITierFeatureLimitRepository tierLimitRepo,
        ITenantSubscriptionOverrideRepository overrideRepo,
        ITenantRepository tenantRepo,
        IInvitationRepository invitationRepo,
        IOptions<TierDefaultOptions> tierDefaults,
        TimeProvider timeProvider,
        ILogger<SubscriptionService> logger)
    {
        ArgumentNullException.ThrowIfNull(subscriptionRepo);
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(alertRuleRepo);
        ArgumentNullException.ThrowIfNull(integrationRepo);
        ArgumentNullException.ThrowIfNull(tierLimitRepo);
        ArgumentNullException.ThrowIfNull(overrideRepo);
        ArgumentNullException.ThrowIfNull(tenantRepo);
        ArgumentNullException.ThrowIfNull(invitationRepo);
        ArgumentNullException.ThrowIfNull(tierDefaults);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _subscriptionRepo = subscriptionRepo;
        _machineRepo = machineRepo;
        _alertRuleRepo = alertRuleRepo;
        _integrationRepo = integrationRepo;
        _tierLimitRepo = tierLimitRepo;
        _overrideRepo = overrideRepo;
        _tenantRepo = tenantRepo;
        _invitationRepo = invitationRepo;
        _tierDefaults = tierDefaults.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<TenantSubscription?> GetSubscriptionForTenantAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        return subscription;
    }

    /// <summary>
    /// Ingest eligibility: an active-or-past-due subscription AND an active tenant. A deactivated
    /// tenant (a pending deletion) never ingests, even on a Free/Active subscription.
    /// </summary>
    internal static bool IsIngestEligible(TenantSubscription? subscription, bool tenantIsActive)
    {
        if (tenantIsActive == false)
        {
            return false;
        }

        // Ingest policy lives here so it has a single home. PastDue is treated as a grace period —
        // ingest continues during Stripe dunning, matching the web app's "PastDue keeps access" behavior.
        // Dunning ends in either recovery (Active) or customer.subscription.deleted, which deactivates and
        // stops ingest. Canceled and no-subscription are not eligible.
        return (subscription is not null) &&
               ((subscription.Status == SubscriptionStatus.Active) || (subscription.Status == SubscriptionStatus.PastDue));
    }

    /// <inheritdoc/>
    public async Task<bool> IsIngestEligibleAsync(int tenantId, CancellationToken ct)
    {
        Tenant? tenant = await _tenantRepo.GetTenantByIdAsync(tenantId, ct);
        if ((tenant is null) || (tenant.IsActive == false))
        {
            return false;
        }

        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        return IsIngestEligible(subscription, tenant.IsActive);
    }

    /// <inheritdoc/>
    public async Task<TenantSubscription> ProvisionFreeSubscriptionAsync(int tenantId, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TenantSubscription subscription = new()
        {
            TenantId = tenantId,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };

        subscription = await _subscriptionRepo.CreateTenantSubscriptionAsync(subscription, ct);
        _logger.LogInformation("Provisioned Free subscription for tenant {TenantId}", tenantId);

        return subscription;
    }

    /// <inheritdoc/>
    public async Task<int> GetRetentionDaysForTenantAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await GetSubscriptionForTenantAsync(tenantId, ct);

        if (subscription is null)
        {
            return 1;
        }

        TenantSubscriptionOverride? tenantOverride = await _overrideRepo.GetOverrideForTenantAsync(tenantId, ct);
        if (tenantOverride?.RetentionDays is not null)
        {
            return tenantOverride.RetentionDays.Value;
        }

        TierFeatureLimit? tierLimits = await _tierLimitRepo.GetLimitsForTierAsync(subscription.Tier, ct);

        return tierLimits?.RetentionDays ?? 1;
    }

    /// <inheritdoc/>
    public async Task<int> GetEffectiveRetentionDaysForTenantAsync(int tenantId, CancellationToken ct)
    {
        return await _subscriptionRepo.GetEffectiveRetentionDaysAsync(tenantId, ct);
    }

    /// <inheritdoc/>
    public async Task<int> GetMachineCountForTenantAsync(int tenantId, CancellationToken ct)
    {
        int count = await _machineRepo.GetActiveMachineCountAsync(tenantId, ct);

        return count;
    }

    /// <inheritdoc/>
    public async Task EnsureSubscriptionExistsAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        if (subscription is not null && subscription.Status == SubscriptionStatus.Active)
        {
            return;
        }

        // PastDue paid subscriptions keep access — Stripe handles dunning and will
        // eventually send a payment_succeeded or subscription_deleted webhook.
        // Do not revert or provision; the tenant retains their current tier.
        if (subscription is not null && subscription.Status == SubscriptionStatus.PastDue)
        {
            _logger.LogDebug("Subscription for tenant {TenantId} is past due — retaining current tier pending payment resolution", tenantId);

            return;
        }

        if (subscription is not null && subscription.Tier == SubscriptionTier.Free && subscription.Status != SubscriptionStatus.Active)
        {
            await _subscriptionRepo.UpdateSubscriptionStateAsync(subscription.TenantId, tier: null, SubscriptionStatus.Active, cancellationToken: ct);

            _logger.LogInformation("Reactivated Free subscription for tenant {TenantId}", tenantId);

            return;
        }

        // Canceled paid subscription — Stripe subscription is gone, revert to Free
        if (subscription is not null && subscription.Status == SubscriptionStatus.Canceled)
        {
            await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, SubscriptionTier.Free, SubscriptionStatus.Active, clearCurrentPeriodEnd: true, cancellationToken: ct);

            _logger.LogInformation("Reverted canceled paid subscription to Free for tenant {TenantId}", tenantId);

            return;
        }

        if (subscription is null)
        {
            await ProvisionFreeSubscriptionAsync(tenantId, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> CanCreateAlertRuleAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        if (subscription is null)
        {
            return false;
        }

        EffectiveLimits limits = await GetEffectiveLimitsForTenantAsync(tenantId, ct);
        int count = await _alertRuleRepo.CountAlertRulesForTenantAsync(tenantId, ct);

        return count < limits.AlertRuleLimit;
    }

    /// <inheritdoc/>
    public async Task<bool> CanCreateWebhookAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        if (subscription is null)
        {
            return false;
        }

        EffectiveLimits limits = await GetEffectiveLimitsForTenantAsync(tenantId, ct);
        int count = await _integrationRepo.CountIntegrationsForTenantAsync(tenantId, ct);

        return count < limits.WebhookLimit;
    }

    /// <inheritdoc/>
    public async Task<bool> CanAddMemberAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        if (subscription is null)
        {
            return false;
        }

        EffectiveLimits limits = await GetEffectiveLimitsForTenantAsync(tenantId, ct);
        int activeMembers = await _tenantRepo.CountActiveMembersAsync(tenantId, ct);
        int pendingInvitations = await _invitationRepo.CountPendingInvitationsAsync(tenantId, _timeProvider.GetUtcNow(), ct);

        return (activeMembers + pendingInvitations) < limits.MemberLimit;
    }

    /// <inheritdoc/>
    public async Task<int> GetMachineCountAtDateAsync(int tenantId, DateTimeOffset targetDate, CancellationToken ct)
    {
        int count = await _machineRepo.GetMachineCountAtDateAsync(tenantId, targetDate, ct);

        return count;
    }

    /// <inheritdoc/>
    public async Task<EffectiveLimits> GetEffectiveLimitsForTenantAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        if (subscription is null)
        {
            TierLimitDefaults freeDefaults = GetConfigDefaultsForTier(SubscriptionTier.Free);

            return new EffectiveLimits
            {
                MachineLimit = freeDefaults.MachineLimit,
                RetentionDays = freeDefaults.RetentionDays,
                AlertRuleLimit = freeDefaults.AlertRuleLimit,
                WebhookLimit = freeDefaults.WebhookLimit,
                MemberLimit = freeDefaults.MemberLimit,
            };
        }

        TierFeatureLimit? tierLimits = await _tierLimitRepo.GetLimitsForTierAsync(subscription.Tier, ct);
        TenantSubscriptionOverride? tenantOverride = await _overrideRepo.GetOverrideForTenantAsync(tenantId, ct);

        if (tierLimits is null)
        {
            // Fallback to configuration defaults when the database row is missing
            _logger.LogWarning("No TierFeatureLimits found for tier {Tier}, using configuration defaults", subscription.Tier);
        }

        TierLimitDefaults configDefaults = GetConfigDefaultsForTier(subscription.Tier);

        return new EffectiveLimits
        {
            MachineLimit = tenantOverride?.MachineLimit ?? tierLimits?.MachineLimit ?? configDefaults.MachineLimit,
            RetentionDays = tenantOverride?.RetentionDays ?? tierLimits?.RetentionDays ?? configDefaults.RetentionDays,
            AlertRuleLimit = tenantOverride?.AlertRuleLimit ?? tierLimits?.AlertRuleLimit ?? configDefaults.AlertRuleLimit,
            WebhookLimit = tenantOverride?.WebhookLimit ?? tierLimits?.WebhookLimit ?? configDefaults.WebhookLimit,
            MemberLimit = tierLimits?.MemberLimit ?? configDefaults.MemberLimit,
        };
    }

    /// <summary>
    /// Gets the configuration-driven default limits for a subscription tier.
    /// </summary>
    private TierLimitDefaults GetConfigDefaultsForTier(SubscriptionTier tier)
    {
        return tier switch
        {
            SubscriptionTier.Free => _tierDefaults.Free,
            SubscriptionTier.Pro => _tierDefaults.Pro,
            SubscriptionTier.Team => _tierDefaults.Team,
            _ => _tierDefaults.Free,
        };
    }
}
