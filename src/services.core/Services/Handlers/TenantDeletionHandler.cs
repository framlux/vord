// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Phase-1 tenant-deletion logic: deactivate now (insert the deletion row, disable the tenant, cancel
/// billing immediately) and the operator-error restore escape hatch. The irreversible purge runs later
/// in <c>TenantPurgeJob</c>.
/// </summary>
public sealed class TenantDeletionHandler
{
    /// <summary>Fixed grace window between deactivation and the irreversible purge.</summary>
    internal static readonly TimeSpan GraceWindow = TimeSpan.FromDays(30);

    private readonly ITenantRepository _tenantRepo;
    private readonly ITenantDeletionRepository _deletionRepo;
    private readonly IAuditLogRepository _auditLog;
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IBillingApiClient _billingApiClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantDeletionHandler> _logger;

    /// <summary>Creates a new instance of the <see cref="TenantDeletionHandler"/> class.</summary>
    /// <param name="tenantRepo">The tenant repository.</param>
    /// <param name="deletionRepo">The tenant-deletion repository.</param>
    /// <param name="auditLog">The audit log repository.</param>
    /// <param name="transactionProvider">The database transaction provider.</param>
    /// <param name="billingApiClient">The billing API client.</param>
    /// <param name="timeProvider">The time provider, for testable "now" resolution.</param>
    /// <param name="logger">The logger.</param>
    public TenantDeletionHandler(
        ITenantRepository tenantRepo,
        ITenantDeletionRepository deletionRepo,
        IAuditLogRepository auditLog,
        IDatabaseTransactionProvider transactionProvider,
        IBillingApiClient billingApiClient,
        TimeProvider timeProvider,
        ILogger<TenantDeletionHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(tenantRepo);
        ArgumentNullException.ThrowIfNull(deletionRepo);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(billingApiClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _tenantRepo = tenantRepo;
        _deletionRepo = deletionRepo;
        _auditLog = auditLog;
        _transactionProvider = transactionProvider;
        _billingApiClient = billingApiClient;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Grace-window arithmetic, isolated for direct unit testing.</summary>
    /// <param name="requestedAt">The instant the deletion was requested.</param>
    internal static DateTimeOffset ComputeScheduledPurge(DateTimeOffset requestedAt)
    {
        return requestedAt.Add(GraceWindow);
    }

    /// <summary>
    /// Decides whether removing this tenant's membership orphans the user. A user is orphaned when
    /// they retain no active role in any tenant after this tenant's roles are removed.
    /// </summary>
    /// <param name="hasActiveRoleElsewhere">Whether the user has an active role in some other tenant.</param>
    internal static bool IsUserOrphanedByTenantRemoval(bool hasActiveRoleElsewhere)
    {
        return hasActiveRoleElsewhere == false;
    }

    /// <summary>Deactivates the tenant and schedules the purge. Guarded against double deletion requests.</summary>
    /// <param name="tenantId">The tenant to deactivate.</param>
    /// <param name="requestedByUserId">The operator requesting the deletion.</param>
    /// <param name="reason">An optional free-text reason captured for the record.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<TenantDeletionResult> RequestDeletionAsync(int tenantId, int requestedByUserId, string? reason, CancellationToken ct)
    {
        Tenant? tenant = await _tenantRepo.GetTenantByIdAsync(tenantId, ct);
        if (tenant is null)
        {
            return new TenantDeletionResult(false, "Tenant not found", null);
        }

        TenantDeletion? existing = await _deletionRepo.GetActiveDeletionForTenantAsync(tenantId, ct);
        if (existing is not null)
        {
            return new TenantDeletionResult(false, "Tenant already has a pending or completed deletion", null);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset scheduledPurgeAt = ComputeScheduledPurge(now);

        // Admin-panel operators authenticate via a separate scheme with no fleet-side Users row, so the
        // billing-api proxy passes 0 as a sentinel for "external admin operator". That value must never
        // land in a Users FK column; null it out here and rely on the billing-api audit trail to record
        // the operator's real identity. The raw value is still preserved on the non-FK
        // TenantDeletions.RequestedByUserId column for record-keeping.
        int? operatorUserId = requestedByUserId > 0 ? requestedByUserId : null;

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _deletionRepo.InsertDeletionAsync(new TenantDeletion
        {
            TenantId = tenantId,
            TenantExternalId = tenant.ExternalId,
            TenantName = tenant.Name,
            RequestedByUserId = requestedByUserId,
            RequestedAt = now,
            ScheduledPurgeAt = scheduledPurgeAt,
            Status = TenantDeletionStatus.Deactivated,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
        }, ct);

        await _deletionRepo.SetTenantActiveAsync(tenantId, false, operatorUserId, now, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId: tenantId,
            userId: operatorUserId,
            machineId: null,
            AuditAction.TenantDeletionRequested,
            AuditResourceType.Tenant,
            tenantId.ToString(),
            new { ScheduledPurgeAt = scheduledPurgeAt, Reason = reason },
            ipAddress: null), ct);

        await transaction.CommitAsync(ct);

        // After commit only — an external call inside the transaction would hold the DB lock across a
        // network round-trip and could not be rolled back anyway. Billing cancel is best-effort: the
        // tenant is already deactivated, and the purge job tears the Stripe customer down entirely at
        // day 30 regardless.
        bool billingCanceled = await _billingApiClient.CancelSubscriptionImmediateAsync(tenant.ExternalId, ct);
        if (billingCanceled == false)
        {
            _logger.LogWarning(
                "Tenant {TenantId} deactivated but immediate billing cancel failed; purge will reconcile at {ScheduledPurgeAt}",
                tenantId, scheduledPurgeAt);
        }

        _logger.LogInformation("Tenant {TenantId} deactivated; purge scheduled for {ScheduledPurgeAt}", tenantId, scheduledPurgeAt);

        return new TenantDeletionResult(true, "OK", scheduledPurgeAt);
    }

    /// <summary>Cancels a pending deletion during the grace window and reactivates the tenant.</summary>
    /// <param name="tenantId">The tenant to restore.</param>
    /// <param name="requestedByUserId">The operator requesting the restore.</param>
    /// <param name="ct">A cancellation token.</param>
    public async Task<TenantDeletionResult> RestoreAsync(int tenantId, int requestedByUserId, CancellationToken ct)
    {
        TenantDeletion? deletion = await _deletionRepo.GetActiveDeletionForTenantAsync(tenantId, ct);
        if (deletion is null)
        {
            return new TenantDeletionResult(false, "No pending deletion for this tenant", null);
        }

        if (deletion.Status == TenantDeletionStatus.Purged)
        {
            return new TenantDeletionResult(false, "Tenant has already been purged and cannot be restored", null);
        }

        // Restore is only safe strictly before the scheduled purge tick fires. Once that tick opens,
        // TenantPurgeJob may have already committed the irreversible fleet-side teardown (deleted
        // operational data, deleted roles, masked orphaned users) and be sitting in a Deactivated
        // status only because the billing cleanup step failed and is awaiting retry. Restoring at or
        // past that point would reactivate a hollow tenant while falsely reporting success, so refuse
        // it even though the row has not yet flipped to Purged.
        if (_timeProvider.GetUtcNow() >= deletion.ScheduledPurgeAt)
        {
            return new TenantDeletionResult(false, "Tenant is at or past its scheduled purge time and can no longer be restored", null);
        }

        // Admin-panel operators have no fleet-side Users row, so 0 is a sentinel for "external admin
        // operator" here too; null it out of the audit's FK column and let the billing-api audit trail
        // carry the operator's real identity.
        int? operatorUserId = requestedByUserId > 0 ? requestedByUserId : null;

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        await _deletionRepo.UpdateDeletionStatusAsync(deletion.Id, TenantDeletionStatus.Restored, null, ct);
        await _deletionRepo.SetTenantActiveAsync(tenantId, true, null, null, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId: tenantId,
            userId: operatorUserId,
            machineId: null,
            AuditAction.TenantRestored,
            AuditResourceType.Tenant,
            tenantId.ToString(),
            new { RestoredDeletionId = deletion.Id },
            ipAddress: null), ct);

        await transaction.CommitAsync(ct);

        _logger.LogInformation("Tenant {TenantId} deletion {DeletionId} restored", tenantId, deletion.Id);

        return new TenantDeletionResult(true, "OK", null);
    }
}
