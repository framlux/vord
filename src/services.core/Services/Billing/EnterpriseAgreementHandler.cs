// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Applies enterprise agreements. Every write that could disagree with the agreement — tier, status,
/// term, limits — happens in one transaction, so a reader never sees Enterprise without its limits.
/// </summary>
public sealed class EnterpriseAgreementHandler : IEnterpriseAgreementHandler
{
    /// <summary>The longest retention an agreement may grant, a product cap rather than a storage limit.</summary>
    public const int MaxRetentionDays = 365;

    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly ISubscriptionRepository _subscriptionRepo;
    private readonly ITenantSubscriptionOverrideRepository _overrideRepo;
    private readonly IAuditLogRepository _auditLog;
    private readonly IBuiltInAlertRuleProvisioner _provisioner;
    private readonly RetentionReclassifyDispatcher _reclassifyDispatcher;
    private readonly ILogger<EnterpriseAgreementHandler> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="EnterpriseAgreementHandler"/> class.
    /// </summary>
    public EnterpriseAgreementHandler(
        IDatabaseTransactionProvider transactionProvider,
        ISubscriptionRepository subscriptionRepo,
        ITenantSubscriptionOverrideRepository overrideRepo,
        IAuditLogRepository auditLog,
        IBuiltInAlertRuleProvisioner provisioner,
        RetentionReclassifyDispatcher reclassifyDispatcher,
        ILogger<EnterpriseAgreementHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(subscriptionRepo);
        ArgumentNullException.ThrowIfNull(overrideRepo);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(reclassifyDispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        _transactionProvider = transactionProvider;
        _subscriptionRepo = subscriptionRepo;
        _overrideRepo = overrideRepo;
        _auditLog = auditLog;
        _provisioner = provisioner;
        _reclassifyDispatcher = reclassifyDispatcher;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<EnterpriseApplyOutcome> ApplyAsync(EnterpriseAgreementTerms terms, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(terms);
        Validate(terms);

        // What the transition may restore depends on where the tenant is coming from, and the write
        // below destroys that, so the prior row is read first.
        TenantSubscription? prior = await _subscriptionRepo.GetSubscriptionForTenantAsync(terms.TenantId, ct);

        EnterpriseApplyOutcome outcome;
        using (IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct))
        {
            outcome = await _subscriptionRepo.ApplyEnterpriseSubscriptionAsync(
                terms.TenantId, terms.Revision, terms.TermEnd, ct);

            if (outcome != EnterpriseApplyOutcome.Applied)
            {
                _logger.LogInformation(
                    "Enterprise agreement {AgreementId} revision {Revision} for tenant {TenantId} not applied: {Outcome}",
                    terms.AgreementId, terms.Revision, terms.TenantId, outcome);

                return outcome;
            }

            await _overrideRepo.UpsertOverrideAsync(
                terms.TenantId, terms.MachineLimit, terms.RetentionDays, terms.AlertRuleLimit,
                terms.WebhookLimit, terms.MemberLimit, ct);

            await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
                terms.TenantId, null, null,
                AuditAction.EnterpriseAgreementApplied, AuditResourceType.Subscription,
                terms.TenantId.ToString(),
                new
                {
                    terms.AgreementId,
                    terms.Revision,
                    terms.MachineLimit,
                    terms.RetentionDays,
                    terms.MemberLimit,
                    terms.AlertRuleLimit,
                    terms.WebhookLimit,
                    terms.TermEnd,
                },
                null), ct);

            await transaction.CommitAsync(ct);
        }

        // The override write does not pass through the caching repository's mutators, so the cached
        // entry (which also caches effective retention) is invalidated here, after commit.
        await _subscriptionRepo.InvalidateSubscriptionCacheAsync(terms.TenantId, ct);
        _reclassifyDispatcher.MarkPending(terms.TenantId);
        _reclassifyDispatcher.DispatchPending();

        await _provisioner.RestoreForTierAsync(
            terms.TenantId, prior, SubscriptionTier.Enterprise, SubscriptionStatus.Active, ct);

        _logger.LogInformation(
            "Enterprise agreement {AgreementId} revision {Revision} applied to tenant {TenantId}",
            terms.AgreementId, terms.Revision, terms.TenantId);

        return outcome;
    }

    internal static void Validate(EnterpriseAgreementTerms terms)
    {
        if (terms.Revision <= 0)
        {
            throw new ArgumentException("Revision must be positive.", nameof(terms));
        }

        if (terms.MachineLimit <= 0)
        {
            throw new ArgumentException("Machine limit must be positive.", nameof(terms));
        }

        if ((terms.RetentionDays < 1) || (terms.RetentionDays > MaxRetentionDays))
        {
            throw new ArgumentException($"Retention must be between 1 and {MaxRetentionDays} days.", nameof(terms));
        }

        if (terms.MemberLimit <= 0)
        {
            throw new ArgumentException("Member limit must be positive.", nameof(terms));
        }

        // Zero alert rules or webhooks is a legitimate contract term ("none allowed"); only the
        // tier-default sentinel and other negatives are refused.
        if ((terms.AlertRuleLimit < 0) || (terms.WebhookLimit < 0))
        {
            throw new ArgumentException("Alert-rule and webhook limits cannot be negative.", nameof(terms));
        }
    }
}
