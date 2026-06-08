// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Alerts;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Commands;
using Framlux.FleetManagement.Services.Core.DataExport;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Hangfire;

namespace Framlux.FleetManagement.Services.Core.Hangfire;

/// <summary>
/// Registers all Vord recurring jobs with Hangfire at services.worker startup.
/// Each phase of the Hangfire migration adds its job to <see cref="RegisterAll"/>.
/// </summary>
public static class RecurringJobRegistry
{
    /// <summary>
    /// Registers every recurring job. Safe to call repeatedly — Hangfire upserts by job id.
    /// Feature-gated jobs are added only when the matching flag is true and removed otherwise so a
    /// previously-registered schedule doesn't fire after the feature is turned off.
    /// </summary>
    /// <param name="recurringJobs">The Hangfire recurring job manager.</param>
    /// <param name="billingEnabled">Whether billing-related jobs should be registered.</param>
    /// <param name="objectStorageEnabled">Whether object-storage-dependent jobs should be registered.</param>
    public static void RegisterAll(IRecurringJobManager recurringJobs, bool billingEnabled, bool objectStorageEnabled)
    {
        ArgumentNullException.ThrowIfNull(recurringJobs);

        recurringJobs.AddOrUpdate<RemoteCommandExpiryJob>(
            RecurringJobIds.RemoteCommandExpiry,
            job => job.RunAsync(CancellationToken.None),
            "* * * * *");

        recurringJobs.AddOrUpdate<PartitionManagementJob>(
            RecurringJobIds.PartitionManagement,
            job => job.RunAsync(CancellationToken.None),
            "0 3 * * *");

        // Health sweep coordinator: enumerates active tenants and enqueues per-tenant
        // HealthSweepTenantJob instances. The predecessor service ran every 15s, but Hangfire's
        // default schedule polling interval is 15s — sub-minute crons would be limited by polling.
        // Run every minute; per-tenant jobs are still fire-and-forget and parallelize across workers.
        recurringJobs.AddOrUpdate<HealthSweepCoordinatorJob>(
            RecurringJobIds.HealthSweepCoordinator,
            job => job.RunAsync(CancellationToken.None),
            "* * * * *");

        // Threshold-based alert evaluation across all paid tenants.
        recurringJobs.AddOrUpdate<AlertEvaluationJob>(
            RecurringJobIds.AlertEvaluation,
            job => job.RunAsync(CancellationToken.None),
            "* * * * *");

        // Daily reaper for orphaned AlertConditionState rows (machines unassigned from rules
        // without firing the condition-clear path).
        recurringJobs.AddOrUpdate<AlertConditionStateCleanupJob>(
            RecurringJobIds.AlertConditionStateCleanup,
            job => job.RunAsync(CancellationToken.None),
            "17 2 * * *");

        if (billingEnabled)
        {
            recurringJobs.AddOrUpdate<UsageHeartbeatJob>(
                RecurringJobIds.UsageHeartbeat,
                job => job.RunAsync(CancellationToken.None),
                "7 * * * *");

            recurringJobs.AddOrUpdate<StripeSyncJob>(
                RecurringJobIds.StripeSync,
                job => job.RunAsync(CancellationToken.None),
                "*/5 * * * *");
        }
        else
        {
            recurringJobs.RemoveIfExists(RecurringJobIds.UsageHeartbeat);
            recurringJobs.RemoveIfExists(RecurringJobIds.StripeSync);
        }

        if (objectStorageEnabled)
        {
            // Recurring fallback for catching pending exports if the on-demand enqueue path is missed.
            // The data-export create endpoint enqueues the same job for sub-minute pickup; the recurring
            // tick is the safety net.
            recurringJobs.AddOrUpdate<DataExportProcessingJob>(
                RecurringJobIds.DataExportProcessing,
                job => job.RunAsync(CancellationToken.None),
                "* * * * *");

            recurringJobs.AddOrUpdate<DataExportCleanupJob>(
                RecurringJobIds.DataExportCleanup,
                job => job.RunAsync(CancellationToken.None),
                "13 * * * *");
        }
        else
        {
            recurringJobs.RemoveIfExists(RecurringJobIds.DataExportProcessing);
            recurringJobs.RemoveIfExists(RecurringJobIds.DataExportCleanup);
        }
    }
}
