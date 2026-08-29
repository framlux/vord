// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles billing webhook events.
/// </summary>
public sealed class BillingWebhookHandler : IBillingWebhookHandler
{
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IAuditLogRepository _auditLog;
    private readonly ISubscriptionRepository _subscriptionRepo;
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IBuiltInAlertRuleProvisioner _builtInProvisioner;
    private readonly IDowngradeCleanupService _downgradeCleanupService;
    private readonly RetentionReclassifyDispatcher _reclassifyDispatcher;

    /// <summary>
    /// Creates a new instance of the <see cref="BillingWebhookHandler"/> class.
    /// </summary>
    public BillingWebhookHandler(
        IDatabaseTransactionProvider transactionProvider,
        IAuditLogRepository auditLog,
        ISubscriptionRepository subscriptionRepo,
        IAlertRuleRepository alertRuleRepo,
        IBuiltInAlertRuleProvisioner builtInProvisioner,
        IDowngradeCleanupService downgradeCleanupService,
        RetentionReclassifyDispatcher reclassifyDispatcher)
    {
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(subscriptionRepo);
        ArgumentNullException.ThrowIfNull(alertRuleRepo);
        ArgumentNullException.ThrowIfNull(builtInProvisioner);
        ArgumentNullException.ThrowIfNull(downgradeCleanupService);
        ArgumentNullException.ThrowIfNull(reclassifyDispatcher);

        _transactionProvider = transactionProvider;
        _auditLog = auditLog;
        _subscriptionRepo = subscriptionRepo;
        _alertRuleRepo = alertRuleRepo;
        _builtInProvisioner = builtInProvisioner;
        _downgradeCleanupService = downgradeCleanupService;
        _reclassifyDispatcher = reclassifyDispatcher;
    }

    /// <inheritdoc/>
    public async Task HandleCheckoutCompletedAsync(int tenantId, SubscriptionTier tier, CancellationToken ct)
    {
        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, tier, SubscriptionStatus.Active, cancellationToken: ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionUpgraded, AuditResourceType.Subscription,
            tenantId.ToString(), null, null), ct);

        await transaction.CommitAsync(ct);

        // Post-commit: a tier change marked during the transaction is only queued now, never inside it.
        _reclassifyDispatcher.DispatchPending();

        // Backstop for tenants created before provisioning moved to tenant creation. Ensure is a
        // no-op for anything already seeded; the enable is what matters, because a prior downgrade
        // to Free disables every rule and nothing else turns them back on.
        await _builtInProvisioner.EnsureProvisionedAsync(tenantId, ct);
        await _builtInProvisioner.EnableBuiltInsAsync(tenantId, ct);

        // Returning to Team restores the custom rules a Pro downgrade disabled. This is the path a
        // real re-upgrade takes — HandleTierCorrectionAsync only runs on Stripe drift, and
        // HandlePaymentSucceededAsync passes a null tier — so without this branch the custom-rule
        // round trip is never repaired on the journey customers actually make.
        if (tier == SubscriptionTier.Team)
        {
            await _alertRuleRepo.EnableCustomAlertRulesAsync(tenantId, ct);
        }
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

        await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, SubscriptionTier.Free, SubscriptionStatus.Active, clearCurrentPeriodEnd: true, cancellationToken: ct);
        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionDowngraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Downgraded to Free tier", null), ct);

        await transaction.CommitAsync(ct);

        // Post-commit: a tier change marked during the transaction is only queued now, never inside it.
        _reclassifyDispatcher.DispatchPending();

        await _downgradeCleanupService.CleanupForFreeTierAsync(tenantId, ct);
    }

    /// <inheritdoc/>
    public async Task HandlePaymentFailedAsync(int tenantId, CancellationToken ct)
    {
        await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, tier: null, SubscriptionStatus.PastDue, cancellationToken: ct);
    }

    /// <inheritdoc/>
    public async Task HandlePaymentSucceededAsync(int tenantId, CancellationToken ct)
    {
        // Billing sends this for every paid invoice, so an ordinary monthly renewal arrives here just
        // as a recovered payment does. The status before the write is the only thing that tells the
        // two apart, and it has to be read before the transaction overwrites it.
        TenantSubscription? priorSubscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);
        bool isRecovery = (priorSubscription is not null) && (priorSubscription.Status != SubscriptionStatus.Active);

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, tier: null, SubscriptionStatus.Active, cancellationToken: ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionUpgraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Payment recovered, subscription reactivated", null), ct);

        await transaction.CommitAsync(ct);

        // Post-commit: a tier change marked during the transaction is only queued now, never inside it.
        _reclassifyDispatcher.DispatchPending();

        // A recovered payment carries no tier of its own, so entitlement has to be read from the row
        // this handler just wrote — Active is now true, which is what makes the policy answer usable.
        // The transition matters because cancellation disables every rule the tenant has while leaving
        // the tier alone, so nothing but this restores them.
        TenantSubscription? subscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        if (SubscriptionPolicy.RequiresPro(subscription) == false)
        {
            // Seeding missing rows never mutates an existing one, so it is safe on every invoice and
            // remains a backstop for a paying tenant that was somehow never provisioned.
            await _builtInProvisioner.EnsureProvisionedAsync(tenantId, ct);

            // The enables overwrite whatever the tenant chose, so they are confined to an actual
            // recovery. Running them on a renewal would revive rules an admin deliberately silenced,
            // once every billing cycle.
            if (isRecovery)
            {
                await _builtInProvisioner.EnableBuiltInsAsync(tenantId, ct);

                // A canceled Team tenant lost its custom rules to the same sweep. Restoring only the
                // built-ins would leave it paying for Team and running on Pro's rule set.
                if (subscription!.Tier == SubscriptionTier.Team)
                {
                    await _alertRuleRepo.EnableCustomAlertRulesAsync(tenantId, ct);
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task HandleDowngradeToProAsync(int tenantId, CancellationToken ct)
    {
        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, SubscriptionTier.Pro, SubscriptionStatus.Active, clearCurrentPeriodEnd: true, cancellationToken: ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionDowngraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Downgraded from Team to Pro", null), ct);

        await transaction.CommitAsync(ct);

        // Post-commit: a tier change marked during the transaction is only queued now, never inside it.
        _reclassifyDispatcher.DispatchPending();
    }

    /// <inheritdoc/>
    public async Task HandleTierCorrectionAsync(int tenantId, SubscriptionTier tier, CancellationToken ct)
    {
        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, tier, SubscriptionStatus.Active, cancellationToken: ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionUpgraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Tier corrected by sync service", null), ct);

        await transaction.CommitAsync(ct);

        // Post-commit: a tier change marked during the transaction is only queued now, never inside it.
        _reclassifyDispatcher.DispatchPending();

        // Drift repair is the one path that exists because a checkout webhook was lost, so it is the
        // likeliest route by which a paying tenant has never been provisioned at all. The provisioner
        // opens its own transaction, which is why this sits after the commit rather than inside it.
        if ((tier == SubscriptionTier.Pro) || (tier == SubscriptionTier.Team))
        {
            await _builtInProvisioner.EnsureProvisionedAsync(tenantId, ct);
            await _builtInProvisioner.EnableBuiltInsAsync(tenantId, ct);

            // Arriving at Team, by any route, thaws the custom rules a Pro downgrade froze.
            if (tier == SubscriptionTier.Team)
            {
                await _alertRuleRepo.EnableCustomAlertRulesAsync(tenantId, ct);
            }
        }
    }

    /// <inheritdoc/>
    public async Task HandleAccountCanceledAsync(int tenantId, CancellationToken ct)
    {
        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, tier: null, SubscriptionStatus.Canceled, cancellationToken: ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionDowngraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Account canceled", null), ct);

        await transaction.CommitAsync(ct);

        // Post-commit: a tier change marked during the transaction is only queued now, never inside it.
        _reclassifyDispatcher.DispatchPending();

        await _downgradeCleanupService.CleanupForFreeTierAsync(tenantId, ct);
    }
}
