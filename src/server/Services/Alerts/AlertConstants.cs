// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

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
}
