// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using System.Globalization;

namespace Framlux.FleetManagement.Services.Core.Billing;

/// <summary>
/// Records, inside the transaction that changed a tenant's machine limit, how many of its hosts that
/// change leaves paused. Pausing has no transition of its own to hang a record on — it is derived —
/// so the limit change is the only moment there is to record it.
/// </summary>
public sealed class HostsPausedAuditWriter
{
    private readonly ISubscriptionService _subscriptionService;
    private readonly IMachineRepository _machineRepo;
    private readonly IAuditLogRepository _auditLog;

    /// <summary>Creates the writer.</summary>
    public HostsPausedAuditWriter(ISubscriptionService subscriptionService, IMachineRepository machineRepo, IAuditLogRepository auditLog)
    {
        ArgumentNullException.ThrowIfNull(subscriptionService);
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(auditLog);

        _subscriptionService = subscriptionService;
        _machineRepo = machineRepo;
        _auditLog = auditLog;
    }

    /// <summary>
    /// Writes a <see cref="AuditAction.HostsPaused"/> entry when the limit the caller just wrote leaves
    /// hosts paused. The limit is read for the tier the caller wrote, never from the cache, which may
    /// still hold the previous tier.
    /// </summary>
    public async Task WriteIfHostsPausedAsync(int tenantId, SubscriptionTier tier, int? userId, CancellationToken ct)
    {
        int limit = await _subscriptionService.GetMachineLimitForTierAsync(tenantId, tier, ct);
        int registered = await _machineRepo.GetActiveMachineCountAsync(tenantId, ct);
        int paused = QuotaPauseService.PausedCount(registered, limit);
        if (paused == 0)
        {
            return;
        }

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, userId, null,
            AuditAction.HostsPaused, AuditResourceType.Subscription,
            tenantId.ToString(CultureInfo.InvariantCulture),
            new { Paused = paused, Limit = limit, Registered = registered },
            null), ct);
    }
}
