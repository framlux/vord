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
        IBuiltInAlertRuleProvisioner builtInProvisioner,
        IDowngradeCleanupService downgradeCleanupService,
        RetentionReclassifyDispatcher reclassifyDispatcher)
    {
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(subscriptionRepo);
        ArgumentNullException.ThrowIfNull(builtInProvisioner);
        ArgumentNullException.ThrowIfNull(downgradeCleanupService);
        ArgumentNullException.ThrowIfNull(reclassifyDispatcher);

        _transactionProvider = transactionProvider;
        _auditLog = auditLog;
        _subscriptionRepo = subscriptionRepo;
        _builtInProvisioner = builtInProvisioner;
        _downgradeCleanupService = downgradeCleanupService;
        _reclassifyDispatcher = reclassifyDispatcher;
    }

    /// <inheritdoc/>
    public async Task HandleCheckoutCompletedAsync(int tenantId, SubscriptionTier tier, CancellationToken ct)
    {
        // What the tenant is entitled to recover depends on where it is coming from, and the write
        // below destroys that. Reading it here is the only chance.
        TenantSubscription? priorSubscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, tier, SubscriptionStatus.Active, cancellationToken: ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionUpgraded, AuditResourceType.Subscription,
            tenantId.ToString(), null, null), ct);

        await transaction.CommitAsync(ct);

        // Post-commit: a tier change marked during the transaction is only queued now, never inside it.
        _reclassifyDispatcher.DispatchPending();

        // Checkout is the journey customers actually make, so it is the path that has to repair a
        // Free downgrade and a Pro-to-Team round trip alike. The provisioner opens its own
        // transaction, which is why this sits after the commit rather than inside it.
        await _builtInProvisioner.RestoreForTierAsync(
            tenantId, priorSubscription, tier, SubscriptionStatus.Active, ct);
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
        // as a recovered payment does. The state before the write is the only thing that tells the
        // two apart, and it has to be read before the transaction overwrites it.
        TenantSubscription? priorSubscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, tier: null, SubscriptionStatus.Active, cancellationToken: ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionUpgraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Payment recovered, subscription reactivated", null), ct);

        await transaction.CommitAsync(ct);

        // Post-commit: a tier change marked during the transaction is only queued now, never inside it.
        _reclassifyDispatcher.DispatchPending();

        // An invoice carries no tier of its own, so the tier is whatever the tenant already held; only
        // the status moved. A tenant with no subscription row was not written to at all above, so
        // there is nothing to restore it to.
        if (priorSubscription is null)
        {
            return;
        }

        await _builtInProvisioner.RestoreForTierAsync(
            tenantId, priorSubscription, priorSubscription.Tier, SubscriptionStatus.Active, ct);
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

        // Nothing runs behind a billing-initiated downgrade, so the freeze belongs here as much as it
        // does in the in-product path. Alert evaluation reads only the enabled flag and the tier the
        // rule requires — it never asks who authored a rule — so a Team-authored rule left enabled
        // keeps firing on a Pro plan. The cleanup opens its own transaction, which is why this sits
        // after the commit rather than inside it.
        await _downgradeCleanupService.CleanupForProTierAsync(tenantId, ct);
    }

    /// <inheritdoc/>
    public async Task HandleTierCorrectionAsync(int tenantId, SubscriptionTier tier, CancellationToken ct)
    {
        TenantSubscription? priorSubscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

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
        await _builtInProvisioner.RestoreForTierAsync(
            tenantId, priorSubscription, tier, SubscriptionStatus.Active, ct);

        // Drift repair reaches Pro by the same route the downgrade does, so it owes the same freeze on
        // the Team-only resources. The sync job refuses to correct downwards to Free, so Pro is the
        // only correction that can take an entitlement away. Restoring first and freezing second is
        // deliberate: the restore never enables custom rules below Team, so the two cannot fight, and
        // freezing last means the losing entitlement has the final word.
        if (tier == SubscriptionTier.Pro)
        {
            await _downgradeCleanupService.CleanupForProTierAsync(tenantId, ct);
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
