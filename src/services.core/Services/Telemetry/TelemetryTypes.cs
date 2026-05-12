// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Telemetry;

/// <summary>
/// Telemetry type identifiers matching the Go agent's db/models.go constants.
/// Used in <see cref="Machines.MachineStateStreamingService"/> and telemetry processing to avoid magic numbers.
/// </summary>
public static class TelemetryTypeIds
{
    /// <summary>System information (hostname, hardware, IPs, uptime).</summary>
    public const short SystemInfo = 1;

    /// <summary>OS name, version, and kernel.</summary>
    public const short OsVersion = 2;

    /// <summary>CPU type and core counts.</summary>
    public const short CpuInfo = 3;

    /// <summary>Memory/swap totals.</summary>
    public const short MemoryInfo = 4;

    /// <summary>Disk layout and mount points.</summary>
    public const short DiskInfo = 5;

    /// <summary>Current CPU usage percentage.</summary>
    public const short CpuUsage = 6;

    /// <summary>Current memory usage bytes and percentage.</summary>
    public const short MemoryUsage = 7;

    /// <summary>Current disk usage per mount.</summary>
    public const short DiskUsage = 8;

    /// <summary>Active SSH sessions.</summary>
    public const short SshSessions = 9;

    /// <summary>Hardware health sensors (temperature, fans, etc.).</summary>
    public const short HardwareHealth = 10;

    /// <summary>Pending package updates.</summary>
    public const short PackageUpdates = 11;

    /// <summary>Systemd service status.</summary>
    public const short ServiceStatus = 12;
}
