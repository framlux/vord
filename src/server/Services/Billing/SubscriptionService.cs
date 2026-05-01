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
    private readonly IWebhookRepository _webhookRepo;
    private readonly SubscriptionOptions _subscriptionOptions;
    private readonly ILogger<SubscriptionService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="SubscriptionService"/> class.
    /// </summary>
    public SubscriptionService(
        ISubscriptionRepository subscriptionRepo,
        IMachineRepository machineRepo,
        IAlertRuleRepository alertRuleRepo,
        IWebhookRepository webhookRepo,
        IOptions<SubscriptionOptions> subscriptionOptions,
        ILogger<SubscriptionService> logger)
    {
        _subscriptionRepo = subscriptionRepo;
        _machineRepo = machineRepo;
        _alertRuleRepo = alertRuleRepo;
        _webhookRepo = webhookRepo;
        _subscriptionOptions = subscriptionOptions.Value;
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

        if (subscription.MachineLimit is null)
        {
            return true;
        }

        int activeMachineCount = await _machineRepo.GetActiveMachineCountAsync(tenantId, ct);

        return activeMachineCount < subscription.MachineLimit.Value;
    }

    /// <inheritdoc/>
    public async Task<TenantSubscription> ProvisionFreeSubscriptionAsync(int tenantId, CancellationToken ct)
    {
        int machineLimit = _subscriptionOptions.FreeTierMachineLimit;
        int retentionDays = _subscriptionOptions.FreeTierRetentionDays;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        TenantSubscription subscription = new()
        {
            TenantId = tenantId,
            Tier = SubscriptionTier.Free,
            Status = SubscriptionStatus.Active,
            MachineLimit = machineLimit,
            RetentionDays = retentionDays,
            AlertRuleLimit = 0,
            WebhookLimit = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };

        subscription = await _subscriptionRepo.InsertSubscriptionAsync(subscription, ct);
        _logger.LogInformation("Provisioned Free subscription for tenant {TenantId} (machines: {MachineLimit}, retention: {RetentionDays}d)", tenantId, machineLimit, retentionDays);

        return subscription;
    }

    /// <inheritdoc/>
    public async Task<int> GetRetentionDaysForTenantAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await GetSubscriptionForTenantAsync(tenantId, ct);

        return subscription?.RetentionDays ?? 1;
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
            int machineLimit = _subscriptionOptions.FreeTierMachineLimit;
            int retentionDays = _subscriptionOptions.FreeTierRetentionDays;

            await _subscriptionRepo.ReactivateFreeSubscriptionAsync(subscription.Id, machineLimit, retentionDays, ct);

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

        if (subscription.AlertRuleLimit is null)
        {
            return true;
        }

        int count = await _alertRuleRepo.CountAlertRulesForTenantAsync(tenantId, ct);

        return count < subscription.AlertRuleLimit.Value;
    }

    /// <inheritdoc/>
    public async Task<bool> CanCreateWebhookAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        if (subscription is null)
        {
            return false;
        }

        if (subscription.WebhookLimit is null)
        {
            return true;
        }

        int count = await _webhookRepo.CountWebhooksForTenantAsync(tenantId, ct);

        return count < subscription.WebhookLimit.Value;
    }

    /// <inheritdoc/>
    public async Task<int> GetMachineCountAtDateAsync(int tenantId, DateTimeOffset targetDate, CancellationToken ct)
    {
        int count = await _machineRepo.GetMachineCountAtDateAsync(tenantId, targetDate, ct);

        return count;
    }
}
