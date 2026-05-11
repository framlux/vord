// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles billing webhook events.
/// </summary>
public sealed class BillingWebhookHandler : IBillingWebhookHandler
{
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IAuditLogRepository _auditLog;
    private readonly ISubscriptionRepository _subscriptionRepo;
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IDowngradeCleanupService _downgradeCleanupService;

    /// <summary>
    /// Creates a new instance of the <see cref="BillingWebhookHandler"/> class.
    /// </summary>
    public BillingWebhookHandler(
        IDatabaseTransactionProvider transactionProvider,
        IAuditLogRepository auditLog,
        ISubscriptionRepository subscriptionRepo,
        IAlertRuleRepository alertRuleRepo,
        IDowngradeCleanupService downgradeCleanupService)
    {
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(subscriptionRepo);
        ArgumentNullException.ThrowIfNull(alertRuleRepo);
        ArgumentNullException.ThrowIfNull(downgradeCleanupService);

        _transactionProvider = transactionProvider;
        _auditLog = auditLog;
        _subscriptionRepo = subscriptionRepo;
        _alertRuleRepo = alertRuleRepo;
        _downgradeCleanupService = downgradeCleanupService;
    }

    /// <inheritdoc/>
    public async Task HandleCheckoutCompletedAsync(int tenantId, SubscriptionTier tier, CancellationToken ct)
    {
        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _subscriptionRepo.UpdateSubscriptionOnCheckoutAsync(tenantId, tier, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionUpgraded, AuditResourceType.Subscription,
            tenantId.ToString(), null, null), ct);

        await transaction.CommitAsync(ct);

        await ProvisionDefaultAlertRulesAsync(tenantId, ct);
    }

    private async Task ProvisionDefaultAlertRulesAsync(int tenantId, CancellationToken ct)
    {
        bool hasRules = await _alertRuleRepo.HasDefaultAlertRulesAsync(tenantId, ct);

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
                DurationMinutes = 5,
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
                DurationMinutes = 1,
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
                DurationMinutes = 1,
                Severity = AlertSeverity.Info,
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
                Name = "CPU usage above 90%",
                Metric = AlertMetric.CpuUsage,
                Operator = AlertOperator.GreaterThan,
                Threshold = 90,
                DurationMinutes = 5,
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
                Name = "Memory usage above 90%",
                Metric = AlertMetric.MemoryUsage,
                Operator = AlertOperator.GreaterThan,
                Threshold = 90,
                DurationMinutes = 5,
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
                Name = "Machine offline",
                Metric = AlertMetric.MachineOffline,
                Operator = AlertOperator.EqualTo,
                Threshold = 1,
                DurationMinutes = 1,
                Severity = AlertSeverity.Critical,
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
                Name = "Disk health issues",
                Metric = AlertMetric.DiskHealth,
                Operator = AlertOperator.EqualTo,
                Threshold = 1,
                DurationMinutes = 1,
                Severity = AlertSeverity.Critical,
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
                Name = "New SSH connection",
                Metric = AlertMetric.SshConnection,
                Operator = AlertOperator.EqualTo,
                Threshold = 1,
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

        await _alertRuleRepo.InsertAlertRulesAsync(defaults, ct);
    }

    /// <inheritdoc/>
    public async Task HandleSubscriptionUpdatedAsync(int tenantId, DateTimeOffset currentPeriodEnd, CancellationToken ct)
    {
        await _subscriptionRepo.UpdateSubscriptionPeriodEndAsync(tenantId, currentPeriodEnd, ct);
    }

    /// <inheritdoc/>
    public async Task HandleSubscriptionDeletedAsync(int tenantId, CancellationToken ct)
    {
        // The billing-api determines the correct downgrade action from its PendingActions table
        // and dispatches the appropriate BillingAction via gRPC. When the action is DowngradeToFree,
        // the subscription is reverted to Free tier. For CancelAccount or unknown actions,
        // the subscription is deactivated entirely.
        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _subscriptionRepo.RevertSubscriptionToFreeAsync(tenantId, ct);
        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionDowngraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Downgraded to Free tier", null), ct);

        await transaction.CommitAsync(ct);

        await _downgradeCleanupService.CleanupForFreeTierAsync(tenantId, ct);
    }

    /// <inheritdoc/>
    public async Task HandlePaymentFailedAsync(int tenantId, CancellationToken ct)
    {
        await _subscriptionRepo.SetSubscriptionPastDueAsync(tenantId, ct);
    }

    /// <inheritdoc/>
    public async Task HandlePaymentSucceededAsync(int tenantId, CancellationToken ct)
    {
        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _subscriptionRepo.SetSubscriptionActiveAsync(tenantId, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionUpgraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Payment recovered, subscription reactivated", null), ct);

        await transaction.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task HandleDowngradeToProAsync(int tenantId, CancellationToken ct)
    {
        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _subscriptionRepo.DowngradeSubscriptionToProAsync(tenantId, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionDowngraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Downgraded from Team to Pro", null), ct);

        await transaction.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task HandleTierCorrectionAsync(int tenantId, SubscriptionTier tier, CancellationToken ct)
    {
        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _subscriptionRepo.UpdateSubscriptionOnCheckoutAsync(tenantId, tier, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionUpgraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Tier corrected by sync service", null), ct);

        await transaction.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task HandleAccountCanceledAsync(int tenantId, CancellationToken ct)
    {
        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _subscriptionRepo.DeactivateSubscriptionAsync(tenantId, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionDowngraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Account canceled", null), ct);

        await transaction.CommitAsync(ct);

        await _downgradeCleanupService.CleanupForFreeTierAsync(tenantId, ct);
    }
}
