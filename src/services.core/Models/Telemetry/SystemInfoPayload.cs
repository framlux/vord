// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Telemetry;

/// <summary>
/// SystemInfo telemetry payload (type=1).
/// </summary>
public sealed class SystemInfoPayload
{
    /// <summary>Machine hostname.</summary>
    public string Hostname { get; set; } = "";

    /// <summary>Machine UUID.</summary>
    public string Uuid { get; set; } = "";

    /// <summary>CPU architecture type.</summary>
    public string CpuType { get; set; } = "";

    /// <summary>CPU brand string.</summary>
    public string CpuBrand { get; set; } = "";

    /// <summary>Physical CPU core count.</summary>
    public int CpuPhysicalCores { get; set; }

    /// <summary>Logical CPU core count.</summary>
    public int CpuLogicalCores { get; set; }

    /// <summary>Total physical memory in bytes.</summary>
    public long PhysicalMemory { get; set; }

    /// <summary>Hardware vendor.</summary>
    public string HardwareVendor { get; set; } = "";

    /// <summary>Hardware model.</summary>
    public string HardwareModel { get; set; } = "";

    /// <summary>Hardware version.</summary>
    public string HardwareVersion { get; set; } = "";

    /// <summary>Hardware serial number.</summary>
    public string HardwareSerial { get; set; } = "";

    /// <summary>System uptime in seconds.</summary>
    public long UptimeSeconds { get; set; }

    /// <summary>BIOS version string.</summary>
    public string BiosVersion { get; set; } = "";

    /// <summary>List of IP addresses.</summary>
    public List<string> IpAddresses { get; set; } = [];
}
