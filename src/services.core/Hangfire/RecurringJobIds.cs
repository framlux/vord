// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Hangfire;

/// <summary>
/// Canonical identifiers for every Vord recurring job. Centralized so an <c>AddOrUpdate</c> id
/// and the matching <c>RemoveIfExists</c> id can never drift apart (a typo previously would
/// leave a stale recurring job in storage that the registry believed it had removed).
/// </summary>
public static class RecurringJobIds
{
    /// <summary>RemoteCommandExpiryJob.</summary>
    public const string RemoteCommandExpiry = "remote-command-expiry";

    /// <summary>PartitionManagementJob.</summary>
    public const string PartitionManagement = "partition-management";

    /// <summary>HealthSweepCoordinatorJob.</summary>
    public const string HealthSweepCoordinator = "health-sweep-coordinator";

    /// <summary>AlertEvaluationJob.</summary>
    public const string AlertEvaluation = "alert-evaluation";

    /// <summary>AlertConditionStateCleanupJob.</summary>
    public const string AlertConditionStateCleanup = "alert-condition-state-cleanup";

    /// <summary>UsageHeartbeatJob (billing-gated).</summary>
    public const string UsageHeartbeat = "usage-heartbeat";

    /// <summary>StripeSyncJob (billing-gated).</summary>
    public const string StripeSync = "stripe-sync";

    /// <summary>DataExportProcessingJob (object-storage-gated).</summary>
    public const string DataExportProcessing = "data-export-processing";

    /// <summary>DataExportCleanupJob (object-storage-gated).</summary>
    public const string DataExportCleanup = "data-export-cleanup";

    /// <summary>Every id, in registration order. Used by audit tests to lock the surface.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        RemoteCommandExpiry,
        PartitionManagement,
        HealthSweepCoordinator,
        AlertEvaluation,
        AlertConditionStateCleanup,
        UsageHeartbeat,
        StripeSync,
        DataExportProcessing,
        DataExportCleanup,
    };
}
