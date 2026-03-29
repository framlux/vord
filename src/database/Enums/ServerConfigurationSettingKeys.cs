// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Enums;

/// <summary>
/// Enumeration of server configuration setting keys.
/// </summary>
public enum ServerConfigurationSettingKeys : int
{
    /// <summary>
    /// Default value, represents no specific setting.
    /// </summary>
    None = 0,

    /// <summary>
    /// Agent heartbeat interval in seconds (default: 300).
    /// </summary>
    AgentHeartbeatSeconds = 1,

    /// <summary>
    /// Agent configuration refresh interval in seconds (default: 21600).
    /// </summary>
    AgentConfigRefreshSeconds = 2,

    /// <summary>
    /// Online threshold in seconds for determining machine online status (default: 300).
    /// </summary>
    OnlineThresholdSeconds = 3,

    /// <summary>
    /// Number of days before certificate expiry to show a warning (default: 30).
    /// </summary>
    CertificateExpiryWarningDays = 4,

    /// <summary>
    /// Grace period in days before permanently deleting soft-deleted telemetry (default: 7).
    /// </summary>
    TelemetryCleanupGraceDays = 5,

    /// <summary>
    /// Deduplication TTL in seconds for telemetry event IDs (default: 300).
    /// </summary>
    DeduplicationTtlSeconds = 6,

    /// <summary>
    /// Agent command poll interval in seconds (default: 30).
    /// </summary>
    AgentCommandPollSeconds = 7,
}
