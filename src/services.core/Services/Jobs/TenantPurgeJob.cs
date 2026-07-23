// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Handlers;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Services.Core.Jobs;

/// <summary>
/// Hangfire recurring job that hard-purges tenants whose 30-day grace has elapsed. Each teardown is
/// idempotent and resumable: every delete is scoped by tenant (already-empty is a no-op) and user
/// masking skips already-masked rows, so a job that dies mid-purge re-runs cleanly from the top.
/// A deletion is marked Purged only when the fleet teardown AND the billing customer-delete both
/// succeed; a billing failure leaves it Deactivated so the next tick retries the billing step.
/// </summary>
public sealed class TenantPurgeJob
{
    private readonly ITenantDeletionRepository _deletionRepo;
    private readonly IAuditLogRepository _auditLog;
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IBillingApiClient _billingApiClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TenantPurgeJob> _logger;

    /// <summary>Creates a new instance of the <see cref="TenantPurgeJob"/> class.</summary>
    /// <param name="deletionRepo">The tenant-deletion repository.</param>
    /// <param name="auditLog">The audit log repository.</param>
    /// <param name="transactionProvider">The database transaction provider.</param>
    /// <param name="billingApiClient">The billing API client.</param>
    /// <param name="timeProvider">The time provider, for testable "now" resolution.</param>
    /// <param name="logger">The logger.</param>
    public TenantPurgeJob(
        ITenantDeletionRepository deletionRepo,
        IAuditLogRepository auditLog,
        IDatabaseTransactionProvider transactionProvider,
        IBillingApiClient billingApiClient,
        TimeProvider timeProvider,
        ILogger<TenantPurgeJob> logger)
    {
        ArgumentNullException.ThrowIfNull(deletionRepo);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(billingApiClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _deletionRepo = deletionRepo;
        _auditLog = auditLog;
        _transactionProvider = transactionProvider;
        _billingApiClient = billingApiClient;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Purges every deletion whose scheduled purge time has passed.</summary>
    /// <param name="ct">Cancellation token (provided by Hangfire on shutdown).</param>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken ct)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<TenantDeletion> due = await _deletionRepo.GetDueDeletionsAsync(now, ct);
        if (due.Count == 0)
        {
            _logger.LogDebug("No tenant deletions due for purge");

            return;
        }

        foreach (TenantDeletion deletion in due)
        {
            try
            {
                await PurgeOneAsync(deletion, ct);
            }
            catch (Exception ex)
            {
                // One tenant's failure must not stop the others; the still-Deactivated row is retried next tick.
                _logger.LogError(ex, "Purge failed for tenant {TenantId} (deletion {DeletionId}); will retry",
                    deletion.TenantId, deletion.Id);
            }
        }
    }

    private async Task PurgeOneAsync(TenantDeletion deletion, CancellationToken ct)
    {
        int tenantId = deletion.TenantId;

        // Snapshot the membership BEFORE deleting roles so the orphan check has the full member set.
        List<int> memberIds = await _deletionRepo.GetUserIdsWithAnyRoleInTenantAsync(tenantId, ct);

        // Fleet-DB teardown in one transaction: operational data, then this tenant's roles, then mask orphans.
        using (IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct))
        {
            await _deletionRepo.PurgeTenantOperationalDataAsync(tenantId, ct);
            await _deletionRepo.DeleteUserTenantRolesForTenantAsync(tenantId, ct);

            foreach (int userId in memberIds)
            {
                bool activeElsewhere = await _deletionRepo.UserHasAnyActiveRoleAsync(userId, ct);
                if (TenantDeletionHandler.IsUserOrphanedByTenantRemoval(activeElsewhere))
                {
                    await _deletionRepo.MaskUserAsync(userId, ct);
                }
            }

            await transaction.CommitAsync(ct);
        }

        // Billing teardown AFTER the fleet commit. If it fails, leave the deletion Deactivated so the next
        // tick retries — nothing is marked complete until fleet AND billing both succeed.
        bool customerDeleted = await _billingApiClient.DeleteCustomerAsync(deletion.TenantExternalId, ct);
        if (customerDeleted == false)
        {
            _logger.LogWarning(
                "Fleet data purged for tenant {TenantId} but billing customer-delete failed; leaving Deactivated for retry",
                tenantId);

            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        await _deletionRepo.UpdateDeletionStatusAsync(deletion.Id, TenantDeletionStatus.Purged, now, ct);

        // Completion audit entry with TenantId = null so it survives any tenant-scoped query after the purge.
        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId: null,
            userId: null,
            machineId: null,
            AuditAction.TenantPurged,
            AuditResourceType.Tenant,
            tenantId.ToString(),
            new { deletion.TenantExternalId, deletion.TenantName, PurgedAt = now },
            ipAddress: null), ct);

        _logger.LogInformation("Tenant {TenantId} ({TenantName}) purged", tenantId, deletion.TenantName);
    }
}
