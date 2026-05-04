// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;

namespace Framlux.FleetManagement.Server.Services.Billing;

/// <summary>
/// Service for managing tenant subscriptions and billing.
/// </summary>
public sealed class SubscriptionService : ISubscriptionService
{
    private readonly ISubscriptionRepository _subscriptionRepo;
    private readonly IMachineRepository _machineRepo;
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IWebhookRepository _webhookRepo;
    private readonly ITierFeatureLimitRepository _tierLimitRepo;
    private readonly ITenantSubscriptionOverrideRepository _overrideRepo;
    private readonly ILogger<SubscriptionService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="SubscriptionService"/> class.
    /// </summary>
    public SubscriptionService(
        ISubscriptionRepository subscriptionRepo,
        IMachineRepository machineRepo,
        IAlertRuleRepository alertRuleRepo,
        IWebhookRepository webhookRepo,
        ITierFeatureLimitRepository tierLimitRepo,
        ITenantSubscriptionOverrideRepository overrideRepo,
        ILogger<SubscriptionService> logger)
    {
        ArgumentNullException.ThrowIfNull(subscriptionRepo);
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(alertRuleRepo);
        ArgumentNullException.ThrowIfNull(webhookRepo);
        ArgumentNullException.ThrowIfNull(tierLimitRepo);
        ArgumentNullException.ThrowIfNull(overrideRepo);
        ArgumentNullException.ThrowIfNull(logger);

        _subscriptionRepo = subscriptionRepo;
        _machineRepo = machineRepo;
        _alertRuleRepo = alertRuleRepo;
        _webhookRepo = webhookRepo;
        _tierLimitRepo = tierLimitRepo;
        _overrideRepo = overrideRepo;
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

        int? machineLimit = await GetEffectiveLimitAsync(
            tenantId, subscription.Tier,
            o => o.MachineLimit,
            t => t.MachineLimit,
            ct);

        if (machineLimit is null)
        {
            return true;
        }

        int activeMachineCount = await _machineRepo.GetActiveMachineCountAsync(tenantId, ct);

        return activeMachineCount < machineLimit.Value;
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

        int? alertRuleLimit = await GetEffectiveLimitAsync(
            tenantId, subscription.Tier,
            o => o.AlertRuleLimit,
            t => t.AlertRuleLimit,
            ct);

        if (alertRuleLimit is null)
        {
            return true;
        }

        int count = await _alertRuleRepo.CountAlertRulesForTenantAsync(tenantId, ct);

        return count < alertRuleLimit.Value;
    }

    /// <inheritdoc/>
    public async Task<bool> CanCreateWebhookAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        if (subscription is null)
        {
            return false;
        }

        int? webhookLimit = await GetEffectiveLimitAsync(
            tenantId, subscription.Tier,
            o => o.WebhookLimit,
            t => t.WebhookLimit,
            ct);

        if (webhookLimit is null)
        {
            return true;
        }

        int count = await _webhookRepo.CountWebhooksForTenantAsync(tenantId, ct);

        return count < webhookLimit.Value;
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
            return new EffectiveLimits { RetentionDays = 1 };
        }

        TierFeatureLimit? tierLimits = await _tierLimitRepo.GetLimitsForTierAsync(subscription.Tier, ct);
        TenantSubscriptionOverride? tenantOverride = await _overrideRepo.GetOverrideForTenantAsync(tenantId, ct);

        return new EffectiveLimits
        {
            MachineLimit = tenantOverride?.MachineLimit ?? tierLimits?.MachineLimit,
            RetentionDays = tenantOverride?.RetentionDays ?? tierLimits?.RetentionDays ?? 1,
            AlertRuleLimit = tenantOverride?.AlertRuleLimit ?? tierLimits?.AlertRuleLimit,
            WebhookLimit = tenantOverride?.WebhookLimit ?? tierLimits?.WebhookLimit,
        };
    }

    /// <summary>
    /// Gets the effective limit for a tenant by checking overrides first, then tier defaults.
    /// Returns null if the limit is unlimited.
    /// </summary>
    private async Task<int?> GetEffectiveLimitAsync(
        int tenantId,
        SubscriptionTier tier,
        Func<TenantSubscriptionOverride, int?> overrideSelector,
        Func<TierFeatureLimit, int?> tierSelector,
        CancellationToken ct)
    {
        TenantSubscriptionOverride? tenantOverride = await _overrideRepo.GetOverrideForTenantAsync(tenantId, ct);
        if (tenantOverride is not null)
        {
            int? overrideValue = overrideSelector(tenantOverride);
            if (overrideValue is not null)
            {
                return overrideValue;
            }
        }

        TierFeatureLimit? tierLimits = await _tierLimitRepo.GetLimitsForTierAsync(tier, ct);
        if (tierLimits is not null)
        {
            return tierSelector(tierLimits);
        }

        // Fallback: no tier limits configured, allow unlimited
        _logger.LogWarning("No TierFeatureLimits found for tier {Tier}, defaulting to unlimited", tier);

        return null;
    }
}
