// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Options;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Services.Billing;

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
    private readonly TierDefaultOptions _tierDefaults;
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
        IOptions<TierDefaultOptions> tierDefaults,
        ILogger<SubscriptionService> logger)
    {
        ArgumentNullException.ThrowIfNull(subscriptionRepo);
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(alertRuleRepo);
        ArgumentNullException.ThrowIfNull(integrationRepo);
        ArgumentNullException.ThrowIfNull(tierLimitRepo);
        ArgumentNullException.ThrowIfNull(overrideRepo);
        ArgumentNullException.ThrowIfNull(tierDefaults);
        ArgumentNullException.ThrowIfNull(logger);

        _subscriptionRepo = subscriptionRepo;
        _machineRepo = machineRepo;
        _alertRuleRepo = alertRuleRepo;
        _integrationRepo = integrationRepo;
        _tierLimitRepo = tierLimitRepo;
        _overrideRepo = overrideRepo;
        _tierDefaults = tierDefaults.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<TenantSubscription?> GetSubscriptionForTenantAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        return subscription;
    }

    /// <inheritdoc/>
    public async Task<bool> CanApproveMachineAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        if (subscription is null)
        {
            return false;
        }

        int machineLimit = await GetEffectiveLimitAsync(
            tenantId, subscription.Tier,
            o => o.MachineLimit,
            t => t.MachineLimit,
            tier => GetConfigDefaultsForTier(tier).MachineLimit,
            ct);

        int activeMachineCount = await _machineRepo.GetActiveMachineCountAsync(tenantId, ct);

        return activeMachineCount < machineLimit;
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

        subscription = await _subscriptionRepo.InsertSubscriptionAsync(subscription, ct);
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

        if (subscription is not null && subscription.Tier == SubscriptionTier.Free && subscription.Status != SubscriptionStatus.Active)
        {
            await _subscriptionRepo.ReactivateFreeSubscriptionAsync(subscription.Id, ct);

            _logger.LogInformation("Reactivated Free subscription for tenant {TenantId}", tenantId);

            return;
        }

        // Canceled paid subscription — Stripe subscription is gone, revert to Free
        if (subscription is not null && subscription.Status == SubscriptionStatus.Canceled)
        {
            await _subscriptionRepo.RevertSubscriptionToFreeAsync(tenantId, ct);

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

        int alertRuleLimit = await GetEffectiveLimitAsync(
            tenantId, subscription.Tier,
            o => o.AlertRuleLimit,
            t => t.AlertRuleLimit,
            tier => GetConfigDefaultsForTier(tier).AlertRuleLimit,
            ct);

        int count = await _alertRuleRepo.CountAlertRulesForTenantAsync(tenantId, ct);

        return count < alertRuleLimit;
    }

    /// <inheritdoc/>
    public async Task<bool> CanCreateWebhookAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        if (subscription is null)
        {
            return false;
        }

        int webhookLimit = await GetEffectiveLimitAsync(
            tenantId, subscription.Tier,
            o => o.WebhookLimit,
            t => t.WebhookLimit,
            tier => GetConfigDefaultsForTier(tier).WebhookLimit,
            ct);

        int count = await _integrationRepo.CountIntegrationsForTenantAsync(tenantId, ct);

        return count < webhookLimit;
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
            };
        }

        TierFeatureLimit? tierLimits = await _tierLimitRepo.GetLimitsForTierAsync(subscription.Tier, ct);
        TenantSubscriptionOverride? tenantOverride = await _overrideRepo.GetOverrideForTenantAsync(tenantId, ct);

        TierLimitDefaults configDefaults = GetConfigDefaultsForTier(subscription.Tier);

        return new EffectiveLimits
        {
            MachineLimit = tenantOverride?.MachineLimit ?? tierLimits?.MachineLimit ?? configDefaults.MachineLimit,
            RetentionDays = tenantOverride?.RetentionDays ?? tierLimits?.RetentionDays ?? configDefaults.RetentionDays,
            AlertRuleLimit = tenantOverride?.AlertRuleLimit ?? tierLimits?.AlertRuleLimit ?? configDefaults.AlertRuleLimit,
            WebhookLimit = tenantOverride?.WebhookLimit ?? tierLimits?.WebhookLimit ?? configDefaults.WebhookLimit,
        };
    }

    /// <summary>
    /// Gets the effective limit for a tenant by checking overrides first, then tier defaults,
    /// then configuration fallback.
    /// </summary>
    private async Task<int> GetEffectiveLimitAsync(
        int tenantId,
        SubscriptionTier tier,
        Func<TenantSubscriptionOverride, int?> overrideSelector,
        Func<TierFeatureLimit, int> tierSelector,
        Func<SubscriptionTier, int> configFallbackSelector,
        CancellationToken ct)
    {
        TenantSubscriptionOverride? tenantOverride = await _overrideRepo.GetOverrideForTenantAsync(tenantId, ct);
        if (tenantOverride is not null)
        {
            int? overrideValue = overrideSelector(tenantOverride);
            if (overrideValue is not null)
            {
                return overrideValue.Value;
            }
        }

        TierFeatureLimit? tierLimits = await _tierLimitRepo.GetLimitsForTierAsync(tier, ct);
        if (tierLimits is not null)
        {
            return tierSelector(tierLimits);
        }

        // Fallback to configuration defaults when the database row is missing
        _logger.LogWarning("No TierFeatureLimits found for tier {Tier}, using configuration defaults", tier);

        return configFallbackSelector(tier);
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
