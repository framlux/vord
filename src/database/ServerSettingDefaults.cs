// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database;

/// <summary>
/// Runtime default values for server configuration settings, used by read paths when a row is
/// missing. The initial migration seeds the same values as frozen literals; a repository test
/// runs the migrations and asserts the seeded rows stay aligned with the enum and with these
/// constants, so the snapshot and the code cannot silently disagree.
/// </summary>
public static class ServerSettingDefaults
{
    /// <summary>Default agent heartbeat interval in seconds.</summary>
    public const int AgentHeartbeatSeconds = 300;

    /// <summary>Default agent configuration refresh interval in seconds.</summary>
    public const int AgentConfigRefreshSeconds = 900;

    /// <summary>Default seconds without a heartbeat before a machine is considered offline.</summary>
    public const int OnlineThresholdSeconds = 300;

    /// <summary>Default deduplication TTL in seconds for telemetry event IDs.</summary>
    public const int DeduplicationTtlSeconds = 300;

    /// <summary>Default agent command poll interval in seconds.</summary>
    public const int AgentCommandPollSeconds = 30;

    /// <summary>Whether new user signup is allowed by default.</summary>
    public const bool AllowUserSignup = true;

    /// <summary>Default fast telemetry collection interval in seconds.</summary>
    public const int TelemetryCollectFastSeconds = 60;

    /// <summary>Default slow telemetry collection interval in seconds.</summary>
    public const int TelemetryCollectSlowSeconds = 900;

    /// <summary>Default fast telemetry send interval in seconds.</summary>
    public const int TelemetrySendFastSeconds = 15;

    /// <summary>Default slow telemetry send interval in seconds.</summary>
    public const int TelemetrySendSlowSeconds = 300;

    /// <summary>Default service status collection interval in seconds.</summary>
    public const int ServiceStatusSeconds = 3600;
}
