// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Server.Services.Alerts;

/// <summary>
/// Shared constants for the alert subsystem including Redis key prefixes and well-known values.
/// </summary>
internal static class AlertConstants
{
    /// <summary>Redis list key for the webhook delivery job queue.</summary>
    internal const string DeliveryQueueKey = "alert:delivery:queue";

    /// <summary>Redis list key for webhook delivery jobs that exhausted all retry attempts.</summary>
    internal const string DeliveryDeadLetterKey = "alert:delivery:deadletter";

    /// <summary>Prefix for Redis keys that track alert condition start times. Format: {prefix}:{ruleId}:{machineId}</summary>
    internal const string ConditionKeyPrefix = "alert:condition";

    /// <summary>The MachineStateSummary.HealthStatus value that represents an offline machine.</summary>
    internal const short HealthStatusOffline = 3;

    /// <summary>
    /// Returns the minimum allowed DurationMinutes for a given alert metric.
    /// Volatile metrics (CPU, Memory, Disk) require a sustained condition.
    /// State metrics require a shorter minimum. Event metrics require zero.
    /// </summary>
    internal static int GetMinimumDurationMinutes(AlertMetric metric)
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
            _ => 1
        };
    }

    /// <summary>
    /// Returns true if the metric is event-based rather than threshold-based.
    /// Event metrics are evaluated at telemetry ingestion time, not in the periodic evaluation loop.
    /// </summary>
    internal static bool IsEventMetric(AlertMetric metric)
    {
        return metric switch
        {
            AlertMetric.SshConnection => true,
            _ => false
        };
    }
}
