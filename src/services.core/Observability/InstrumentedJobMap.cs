// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Hangfire;
using System.Collections.Frozen;

namespace Framlux.FleetManagement.Services.Core.Observability;

/// <summary>
/// Resolves a background job run to the closed vocabulary the metrics tag it with.
/// </summary>
/// <remarks>
/// Two sources are needed because neither alone covers both kinds of job. A recurring job carries its
/// id as a Hangfire job parameter and its type name is unrelated to that id; a fire-and-forget job has
/// no recurring id at all. Resolving from the id alone would put every invitation email and every
/// integration delivery into <see cref="InstrumentedJob.Other"/> — the jobs an operator most wants
/// timed.
/// </remarks>
public static class InstrumentedJobMap
{
    private static readonly FrozenDictionary<string, InstrumentedJob> ByRecurringId =
        new Dictionary<string, InstrumentedJob>(StringComparer.Ordinal)
        {
            [RecurringJobIds.RemoteCommandExpiry] = InstrumentedJob.RemoteCommandExpiry,
            [RecurringJobIds.PartitionManagement] = InstrumentedJob.PartitionManagement,
            [RecurringJobIds.HealthSweepCoordinator] = InstrumentedJob.HealthSweepCoordinator,
            [RecurringJobIds.AlertEvaluation] = InstrumentedJob.AlertEvaluation,
            [RecurringJobIds.AlertConditionStateCleanup] = InstrumentedJob.AlertConditionStateCleanup,
            [RecurringJobIds.TenantPurge] = InstrumentedJob.TenantPurge,
            [RecurringJobIds.StripeSync] = InstrumentedJob.StripeSync,
            [RecurringJobIds.DataExportProcessing] = InstrumentedJob.DataExportProcessing,
            [RecurringJobIds.DataExportCleanup] = InstrumentedJob.DataExportCleanup,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, InstrumentedJob> ByTypeName =
        new Dictionary<string, InstrumentedJob>(StringComparer.Ordinal)
        {
            ["RemoteCommandExpiryJob"] = InstrumentedJob.RemoteCommandExpiry,
            ["PartitionManagementJob"] = InstrumentedJob.PartitionManagement,
            ["HealthSweepCoordinatorJob"] = InstrumentedJob.HealthSweepCoordinator,
            ["AlertEvaluationJob"] = InstrumentedJob.AlertEvaluation,
            ["AlertConditionStateCleanupJob"] = InstrumentedJob.AlertConditionStateCleanup,
            ["TenantPurgeJob"] = InstrumentedJob.TenantPurge,
            ["StripeSyncJob"] = InstrumentedJob.StripeSync,
            ["DataExportProcessingJob"] = InstrumentedJob.DataExportProcessing,
            ["DataExportCleanupJob"] = InstrumentedJob.DataExportCleanup,
            ["HealthSweepTenantJob"] = InstrumentedJob.HealthSweepTenant,
            ["IntegrationDeliveryJob"] = InstrumentedJob.IntegrationDelivery,
            ["RetentionReclassifyJob"] = InstrumentedJob.RetentionReclassify,
            ["SendInvitationEmailJob"] = InstrumentedJob.SendInvitationEmail,
            ["SshAlertEvaluationJob"] = InstrumentedJob.SshAlertEvaluation,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Resolves a job run to its vocabulary member.
    /// </summary>
    /// <param name="recurringJobId">The Hangfire recurring job id, or null for a fire-and-forget job.</param>
    /// <param name="typeName">The job class name, used when there is no recurring id.</param>
    /// <returns>The member naming this job, or <see cref="InstrumentedJob.Other"/>.</returns>
    public static InstrumentedJob Resolve(string? recurringJobId, string? typeName)
    {
        if ((string.IsNullOrWhiteSpace(recurringJobId) == false) &&
            (ByRecurringId.TryGetValue(recurringJobId, out InstrumentedJob byId) == true))
        {
            return byId;
        }

        if ((string.IsNullOrWhiteSpace(typeName) == false) &&
            (ByTypeName.TryGetValue(typeName, out InstrumentedJob byType) == true))
        {
            return byType;
        }

        return InstrumentedJob.Other;
    }

    /// <summary>
    /// Resolves a recurring job id on its own, for the health gauge, which has no type name.
    /// </summary>
    /// <param name="recurringJobId">The Hangfire recurring job id.</param>
    /// <returns>The member naming this job, or <see cref="InstrumentedJob.Other"/>.</returns>
    public static InstrumentedJob ResolveRecurring(string recurringJobId)
    {
        return Resolve(recurringJobId, typeName: null);
    }
}
