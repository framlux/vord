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
    /// Deduplication TTL in seconds for telemetry event IDs (default: 300).
    /// </summary>
    DeduplicationTtlSeconds = 6,

    /// <summary>
    /// Agent command poll interval in seconds (default: 30).
    /// </summary>
    AgentCommandPollSeconds = 7,

    /// <summary>
    /// Whether new users are allowed to self-register via social login (default: true).
    /// When false, only users who already exist in the database can sign in.
    /// </summary>
    AllowUserSignup = 8,

    /// <summary>
    /// Fast telemetry collection interval in seconds (default: 30, range: 10-300).
    /// Controls how often the agent samples CPU, memory, and disk usage.
    /// </summary>
    TelemetryCollectFastSeconds = 9,

    /// <summary>
    /// Slow telemetry collection interval in seconds (default: 900, range: 60-3600).
    /// Controls how often the agent collects static system information.
    /// </summary>
    TelemetryCollectSlowSeconds = 10,

    /// <summary>
    /// Fast telemetry send interval in seconds (default: 15, range: 5-120).
    /// Controls how often the agent transmits high-frequency metrics to the server.
    /// </summary>
    TelemetrySendFastSeconds = 11,

    /// <summary>
    /// Slow telemetry send interval in seconds (default: 300, range: 30-1800).
    /// Controls how often the agent transmits low-frequency data to the server.
    /// </summary>
    TelemetrySendSlowSeconds = 12,

    /// <summary>
    /// High-water mark (MachineTelemetry.Id) for the streaming state service.
    /// Used to track which telemetry rows have been processed into the summary tables.
    /// </summary>
    StreamingHighWaterMark = 13,
}
