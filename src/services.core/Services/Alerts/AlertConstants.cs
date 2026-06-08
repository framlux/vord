// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Services.Core.Alerts;

/// <summary>
/// Shared constants for the alert subsystem including Redis key prefixes and well-known values.
/// </summary>
public static class AlertConstants
{
    // Note: the former DeliveryQueueKey ("alert:delivery:queue") and DeliveryDeadLetterKey
    // ("alert:delivery:deadletter") constants were removed when integration delivery migrated to
    // Hangfire's native enqueue + [AutomaticRetry]; failed deliveries land in the Hangfire
    // dashboard's Failed tab. The former ConditionKeyPrefix ("alert:condition") was removed when
    // AlertEvaluation migrated to the AlertConditionStates Postgres table.

    /// <summary>The MachineStateSummary.HealthStatus value that represents an offline machine.</summary>
    public const short HealthStatusOffline = 3;

    /// <summary>
    /// Maximum DurationMinutes an alert rule can configure. Validators must enforce this upper
    /// bound; <see cref="ConditionStateRetentionWindow"/> sizes the AlertConditionStates
    /// reaper window accordingly. Raising this constant requires reviewing the safety margin.
    /// </summary>
    public const int MaxRuleDurationMinutes = 1440;

    /// <summary>
    /// Safety margin added to <see cref="MaxRuleDurationMinutes"/> when sizing the
    /// AlertConditionStates retention window. Prevents the reaper from deleting a row mid-window
    /// even under realistic clock drift between application and database.
    /// </summary>
    public const int ConditionStateRetentionSafetyMarginMinutes = 15;

    /// <summary>
    /// Retention window for <c>AlertConditionStates</c> rows — kept strictly above the largest
    /// DurationMinutes a rule can configure so the reaper never deletes an in-progress window.
    /// </summary>
    public static TimeSpan ConditionStateRetentionWindow
        => TimeSpan.FromMinutes(MaxRuleDurationMinutes + ConditionStateRetentionSafetyMarginMinutes);

    /// <summary>
    /// Returns the minimum allowed DurationMinutes for a given alert metric.
    /// Volatile metrics (CPU, Memory, Disk) require a sustained condition.
    /// State metrics require a shorter minimum. Event metrics require zero.
    /// </summary>
    public static int GetMinimumDurationMinutes(AlertMetric metric)
    {
        return metric switch
        {
            AlertMetric.CpuUsage => 5,
            AlertMetric.MemoryUsage => 5,
            AlertMetric.DiskUsage => 5,
            AlertMetric.MachineOffline => 1,
            AlertMetric.FailedServices => 1,
            AlertMetric.SecurityUpdates => 1,
            AlertMetric.DiskHealth => 1,
            AlertMetric.SshConnection => 0,
            _ => 1,
        };
    }

    /// <summary>
    /// Returns true if the metric is event-based rather than threshold-based.
    /// Event metrics are evaluated at telemetry ingestion time, not in the periodic evaluation loop.
    /// </summary>
    public static bool IsEventMetric(AlertMetric metric)
    {
        return metric switch
        {
            AlertMetric.SshConnection => true,
            _ => false,
        };
    }
}
