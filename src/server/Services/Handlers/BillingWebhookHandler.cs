// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Cache;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Options;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using LinqToDB.Async;
using LinqToDB;
using Microsoft.Extensions.Options;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles billing webhook events.
/// </summary>
public sealed class BillingWebhookHandler : IBillingWebhookHandler
{
    private readonly IDatabaseCache _databaseCache;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDowngradeCleanupService _downgradeCleanupService;
    private readonly SubscriptionOptions _subscriptionOptions;

    /// <summary>
    /// Creates a new instance of the <see cref="BillingWebhookHandler"/> class.
    /// </summary>
    public BillingWebhookHandler(IDatabaseCache databaseCache, IServiceScopeFactory scopeFactory, IDowngradeCleanupService downgradeCleanupService, IOptions<SubscriptionOptions> subscriptionOptions)
    {
        _databaseCache = databaseCache;
        _scopeFactory = scopeFactory;
        _downgradeCleanupService = downgradeCleanupService;
        _subscriptionOptions = subscriptionOptions.Value;
    }

    /// <inheritdoc/>
    public async Task HandleCheckoutCompletedAsync(int tenantId, SubscriptionTier tier, CancellationToken ct)
    {
        using IDatabaseTransaction transaction = await _databaseCache.BeginTransactionAsync(ct);

        int? alertRuleLimit = tier == SubscriptionTier.Team ? _subscriptionOptions.TeamTierAlertRuleLimit : _subscriptionOptions.ProTierAlertRuleLimit;
        int? webhookLimit = tier == SubscriptionTier.Team ? _subscriptionOptions.TeamTierWebhookLimit : _subscriptionOptions.ProTierWebhookLimit;
        await _databaseCache.UpdateSubscriptionOnCheckoutAsync(tenantId, tier, alertRuleLimit, webhookLimit, ct);

        await _databaseCache.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionUpgraded, AuditResourceType.Subscription,
            tenantId.ToString(), null, null), ct);

        await transaction.CommitAsync(ct);

        await ProvisionDefaultAlertRulesAsync(tenantId, ct);
    }

    private async Task ProvisionDefaultAlertRulesAsync(int tenantId, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        // Check if default rules already exist for this tenant
        bool hasRules = await db.AlertRules
            .AnyAsync(r => r.TenantId == tenantId && r.IsCustom == false, ct);

        if (hasRules)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;

        AlertRule[] defaults =
        [
            new()
            {
                TenantId = tenantId,
                Name = "Disk usage above 90%",
                Metric = AlertMetric.DiskUsage,
                Operator = AlertOperator.GreaterThan,
                Threshold = 90,
                DurationMinutes = 0,
                Severity = AlertSeverity.Warning,
                IsEnabled = true,
                NotifyEmail = true,
                NotifyWebhook = false,
                IsCustom = false,
                CreatedByUserId = 1,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new()
            {
                TenantId = tenantId,
                Name = "Failed services detected",
                Metric = AlertMetric.FailedServices,
                Operator = AlertOperator.GreaterThan,
                Threshold = 0,
                DurationMinutes = 0,
                Severity = AlertSeverity.Warning,
                IsEnabled = true,
                NotifyEmail = true,
                NotifyWebhook = false,
                IsCustom = false,
                CreatedByUserId = 1,
                CreatedAt = now,
                UpdatedAt = now,
            },
            new()
            {
                TenantId = tenantId,
                Name = "Security updates available",
                Metric = AlertMetric.SecurityUpdates,
                Operator = AlertOperator.GreaterThan,
                Threshold = 0,
                DurationMinutes = 0,
                Severity = AlertSeverity.Info,
                IsEnabled = true,
                NotifyEmail = true,
                NotifyWebhook = false,
                IsCustom = false,
                CreatedByUserId = 1,
                CreatedAt = now,
                UpdatedAt = now,
            },
        ];

        foreach (AlertRule rule in defaults)
        {
            await db.InsertAsync(rule, token: ct);
        }
    }

    /// <inheritdoc/>
    public async Task HandleSubscriptionUpdatedAsync(int tenantId, DateTimeOffset currentPeriodEnd, CancellationToken ct)
    {
        await _databaseCache.UpdateSubscriptionPeriodEndAsync(tenantId, currentPeriodEnd, ct);
    }

    /// <inheritdoc/>
    public async Task HandleSubscriptionDeletedAsync(int tenantId, CancellationToken ct)
    {
        TenantSubscription? subscription = await _databaseCache.GetSubscriptionForTenantAsync(tenantId, ct);
        PendingSubscriptionAction pendingAction = subscription?.PendingAction ?? PendingSubscriptionAction.None;

        switch (pendingAction)
        {
            case PendingSubscriptionAction.DowngradeToFree:
            {
                using IDatabaseTransaction transaction = await _databaseCache.BeginTransactionAsync(ct);

                await _databaseCache.RevertSubscriptionToFreeAsync(tenantId, _subscriptionOptions.FreeTierMachineLimit, _subscriptionOptions.FreeTierRetentionDays, 0, 0, ct);
                await _databaseCache.InsertAuditLogAsync(AuditHelper.Create(
                    tenantId, null, null,
                    AuditAction.SubscriptionDowngraded, AuditResourceType.Subscription,
                    tenantId.ToString(), "Downgraded to Free tier", null), ct);

                await transaction.CommitAsync(ct);

                await _downgradeCleanupService.CleanupForFreeTierAsync(tenantId, ct);

                break;
            }

            case PendingSubscriptionAction.DowngradeToPro:
            {
                using IDatabaseTransaction transaction = await _databaseCache.BeginTransactionAsync(ct);

                await _databaseCache.DowngradeSubscriptionToProAsync(tenantId, _subscriptionOptions.ProTierAlertRuleLimit, _subscriptionOptions.ProTierWebhookLimit, ct);
                await _databaseCache.InsertAuditLogAsync(AuditHelper.Create(
                    tenantId, null, null,
                    AuditAction.SubscriptionDowngraded, AuditResourceType.Subscription,
                    tenantId.ToString(), "Downgraded to Pro tier", null), ct);

                await transaction.CommitAsync(ct);

                await _downgradeCleanupService.CleanupForProTierAsync(tenantId, ct);

                break;
            }

            case PendingSubscriptionAction.CancelAccount:
            case PendingSubscriptionAction.None:
            default:
            {
                using IDatabaseTransaction transaction = await _databaseCache.BeginTransactionAsync(ct);

                await _databaseCache.DeactivateSubscriptionAsync(tenantId, ct);
                await _databaseCache.InsertAuditLogAsync(AuditHelper.Create(
                    tenantId, null, null,
                    AuditAction.SubscriptionCanceled, AuditResourceType.Subscription,
                    tenantId.ToString(), null, null), ct);

                await transaction.CommitAsync(ct);

                break;
            }
        }
    }

    /// <inheritdoc/>
    public async Task HandlePaymentFailedAsync(int tenantId, CancellationToken ct)
    {
        await _databaseCache.SetSubscriptionPastDueAsync(tenantId, ct);
    }

    /// <inheritdoc/>
    public async Task HandlePaymentSucceededAsync(int tenantId, CancellationToken ct)
    {
        using IDatabaseTransaction transaction = await _databaseCache.BeginTransactionAsync(ct);

        await _databaseCache.SetSubscriptionActiveAsync(tenantId, ct);

        await _databaseCache.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionUpgraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Payment recovered, subscription reactivated", null), ct);

        await transaction.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task HandleDowngradeToProAsync(int tenantId, CancellationToken ct)
    {
        using IDatabaseTransaction transaction = await _databaseCache.BeginTransactionAsync(ct);

        await _databaseCache.DowngradeSubscriptionToProAsync(tenantId, _subscriptionOptions.ProTierAlertRuleLimit, _subscriptionOptions.ProTierWebhookLimit, ct);

        await _databaseCache.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionDowngraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Downgraded from Team to Pro", null), ct);

        await transaction.CommitAsync(ct);
    }
}
