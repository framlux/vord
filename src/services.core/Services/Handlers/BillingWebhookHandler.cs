// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<BillingWebhookHandler> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="BillingWebhookHandler"/> class.
    /// </summary>
    public BillingWebhookHandler(
        IDatabaseTransactionProvider transactionProvider,
        IAuditLogRepository auditLog,
        ISubscriptionRepository subscriptionRepo,
        IBuiltInAlertRuleProvisioner builtInProvisioner,
        IDowngradeCleanupService downgradeCleanupService,
        RetentionReclassifyDispatcher reclassifyDispatcher,
        ILogger<BillingWebhookHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(subscriptionRepo);
        ArgumentNullException.ThrowIfNull(builtInProvisioner);
        ArgumentNullException.ThrowIfNull(downgradeCleanupService);
        ArgumentNullException.ThrowIfNull(reclassifyDispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        _transactionProvider = transactionProvider;
        _auditLog = auditLog;
        _subscriptionRepo = subscriptionRepo;
        _builtInProvisioner = builtInProvisioner;
        _downgradeCleanupService = downgradeCleanupService;
        _reclassifyDispatcher = reclassifyDispatcher;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task HandleCheckoutCompletedAsync(int tenantId, SubscriptionTier tier, CancellationToken ct)
    {
        // What the tenant is entitled to recover depends on where it is coming from, and the write
        // below destroys that. Reading it here is the only chance.
        TenantSubscription? priorSubscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        int updated = await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, tier, SubscriptionStatus.Active, cancellationToken: ct);
        if (updated == 0)
        {
            // No row changed: the tenant has no subscription, or is on an Enterprise agreement that
            // Stripe does not own. Either way there is no upgrade to record or provision for.
            _logger.LogInformation(
                "Billing: checkout completion for tenant {TenantId} changed no subscription; skipping the upgrade", tenantId);

            return;
        }

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
        int updated = await _subscriptionRepo.UpdateSubscriptionPeriodEndAsync(tenantId, currentPeriodEnd, ct);
        if (updated == 0)
        {
            // An Enterprise tenant's period end is its agreement term, which Stripe does not own.
            _logger.LogDebug(
                "Billing: period end update for tenant {TenantId} changed no subscription", tenantId);
        }
    }

    /// <inheritdoc/>
    public async Task HandleSubscriptionDeletedAsync(int tenantId, CancellationToken ct)
    {
        // The billing-api determines the correct downgrade action from its PendingActions table
        // and dispatches the appropriate BillingAction via gRPC. When the action is DowngradeToFree,
        // the subscription is reverted to Free tier. For CancelAccount or unknown actions,
        // the subscription is deactivated entirely.
        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        int updated = await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, SubscriptionTier.Free, SubscriptionStatus.Active, clearCurrentPeriodEnd: true, cancellationToken: ct);
        if (updated == 0)
        {
            // No row changed: the tenant has no subscription, or is on an Enterprise agreement that
            // Stripe does not own. Either way there is nothing to clean up.
            _logger.LogInformation(
                "Billing: subscription deletion for tenant {TenantId} changed no subscription; skipping the Free downgrade", tenantId);

            return;
        }

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionDowngraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Downgraded to Free tier", null), ct);

        // The cleanup is irreversible (machines beyond the Free limit are soft-deleted), so it runs
        // inside this transaction: the tier write above holds the subscription row's lock until the
        // commit, which makes an agreement being applied to this tenant wait for the cleanup instead of
        // landing between a committed Free tier and a cleanup that would then hit an Enterprise tenant.
        IReadOnlyList<string> trimmedApiKeyHashes = await _downgradeCleanupService.CleanupForFreeTierAsync(tenantId, ct);

        await transaction.CommitAsync(ct);

        // Post-commit: a tier change marked during the transaction is only queued now, never inside it.
        _reclassifyDispatcher.DispatchPending();

        // Evicting before the commit would let a request that still sees the machine as active cache its
        // key again, so the trimmed machines keep authenticating until the entry expires.
        await _downgradeCleanupService.EvictApiKeysAsync(trimmedApiKeyHashes, ct);
    }

    /// <inheritdoc/>
    public async Task HandlePaymentFailedAsync(int tenantId, CancellationToken ct)
    {
        int updated = await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, tier: null, SubscriptionStatus.PastDue, cancellationToken: ct);
        if (updated == 0)
        {
            // No row changed: the tenant has no subscription, or is on an Enterprise agreement that
            // is invoiced outside Stripe and so cannot fall into dunning.
            _logger.LogInformation(
                "Billing: payment failure for tenant {TenantId} changed no subscription; not marking it past due", tenantId);
        }
    }

    /// <inheritdoc/>
    public async Task HandlePaymentSucceededAsync(int tenantId, CancellationToken ct)
    {
        // Billing sends this for every paid invoice, so an ordinary monthly renewal arrives here just
        // as a recovered payment does. The state before the write is the only thing that tells the
        // two apart, and it has to be read before the transaction overwrites it.
        TenantSubscription? priorSubscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        int updated = await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, tier: null, SubscriptionStatus.Active, cancellationToken: ct);
        if (updated == 0)
        {
            // No row changed: the tenant has no subscription, or is on an Enterprise agreement whose
            // status Stripe does not own. There is no recovered payment to record or restore for.
            _logger.LogInformation(
                "Billing: payment success for tenant {TenantId} changed no subscription; skipping the reactivation", tenantId);

            return;
        }

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionUpgraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Payment recovered, subscription reactivated", null), ct);

        await transaction.CommitAsync(ct);

        // Post-commit: a tier change marked during the transaction is only queued now, never inside it.
        _reclassifyDispatcher.DispatchPending();

        // An invoice carries no tier of its own, so the tier is whatever the tenant already held; only
        // the status moved. A row that only appeared after the read above has no prior state to
        // restore from.
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

        int updated = await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, SubscriptionTier.Pro, SubscriptionStatus.Active, clearCurrentPeriodEnd: true, cancellationToken: ct);
        if (updated == 0)
        {
            // No row changed: the tenant has no subscription, or is on an Enterprise agreement that
            // Stripe does not own. Either way there are no Team-only resources to freeze.
            _logger.LogInformation(
                "Billing: downgrade to Pro for tenant {TenantId} changed no subscription; skipping the freeze", tenantId);

            return;
        }

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionDowngraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Downgraded from Team to Pro", null), ct);

        // Nothing runs behind a billing-initiated downgrade, so the freeze belongs here as much as it
        // does in the in-product path. Alert evaluation reads only the enabled flag and the tier the
        // rule requires — it never asks who authored a rule — so a Team-authored rule left enabled
        // keeps firing on a Pro plan. It runs inside this transaction so the tier write's row lock
        // keeps an agreement being applied from landing between the committed tier and the freeze.
        await _downgradeCleanupService.CleanupForProTierAsync(tenantId, ct);

        await transaction.CommitAsync(ct);

        // Post-commit: a tier change marked during the transaction is only queued now, never inside it.
        _reclassifyDispatcher.DispatchPending();
    }

    /// <inheritdoc/>
    public async Task HandleTierCorrectionAsync(int tenantId, SubscriptionTier tier, CancellationToken ct)
    {
        TenantSubscription? priorSubscription = await _subscriptionRepo.GetSubscriptionForTenantAsync(tenantId, ct);

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        int updated = await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, tier, SubscriptionStatus.Active, cancellationToken: ct);
        if (updated == 0)
        {
            // No row changed: the tenant has no subscription, or is on an Enterprise agreement that
            // Stripe does not own. Either way there is no drift to repair.
            _logger.LogInformation(
                "Billing: tier correction for tenant {TenantId} changed no subscription; skipping the repair", tenantId);

            return;
        }

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionUpgraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Tier corrected by sync service", null), ct);

        // Drift repair reaches Pro by the same route the downgrade does, so it owes the same freeze on
        // the Team-only resources, inside this transaction for the same reason. The sync job refuses to
        // correct downwards to Free, so Pro is the only correction that can take an entitlement away.
        // The restore below never enables custom rules below Team, so it cannot undo the freeze.
        if (tier == SubscriptionTier.Pro)
        {
            await _downgradeCleanupService.CleanupForProTierAsync(tenantId, ct);
        }

        await transaction.CommitAsync(ct);

        // Post-commit: a tier change marked during the transaction is only queued now, never inside it.
        _reclassifyDispatcher.DispatchPending();

        // Drift repair is the one path that exists because a checkout webhook was lost, so it is the
        // likeliest route by which a paying tenant has never been provisioned at all.
        await _builtInProvisioner.RestoreForTierAsync(
            tenantId, priorSubscription, tier, SubscriptionStatus.Active, ct);
    }

    /// <inheritdoc/>
    public async Task HandleAccountCanceledAsync(int tenantId, CancellationToken ct)
    {
        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        int updated = await _subscriptionRepo.UpdateSubscriptionStateAsync(tenantId, tier: null, SubscriptionStatus.Canceled, cancellationToken: ct);
        if (updated == 0)
        {
            // No row changed: the tenant has no subscription, or is on an Enterprise agreement that
            // Stripe does not own. Either way there is nothing to cancel or clean up.
            _logger.LogInformation(
                "Billing: account cancellation for tenant {TenantId} changed no subscription; skipping the cleanup", tenantId);

            return;
        }

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, null, null,
            AuditAction.SubscriptionDowngraded, AuditResourceType.Subscription,
            tenantId.ToString(), "Account canceled", null), ct);

        // Inside the transaction for the reason given in HandleSubscriptionDeletedAsync.
        IReadOnlyList<string> trimmedApiKeyHashes = await _downgradeCleanupService.CleanupForFreeTierAsync(tenantId, ct);

        await transaction.CommitAsync(ct);

        // Post-commit: a tier change marked during the transaction is only queued now, never inside it.
        _reclassifyDispatcher.DispatchPending();

        await _downgradeCleanupService.EvictApiKeysAsync(trimmedApiKeyHashes, ct);
    }
}
