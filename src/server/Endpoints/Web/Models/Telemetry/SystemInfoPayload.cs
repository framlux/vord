// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json.Serialization;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// SystemInfo telemetry payload (type=1).
/// </summary>
public sealed class SystemInfoPayload
{
    /// <summary>Machine hostname.</summary>
    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = "";

    /// <summary>Machine UUID.</summary>
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; } = "";

    /// <summary>CPU architecture type.</summary>
    [JsonPropertyName("cpu_type")]
    public string CpuType { get; set; } = "";

    /// <summary>CPU brand string.</summary>
    [JsonPropertyName("cpu_brand")]
    public string CpuBrand { get; set; } = "";

    /// <summary>Physical CPU core count.</summary>
    [JsonPropertyName("cpu_physical_cores")]
    public int CpuPhysicalCores { get; set; }

    /// <summary>Logical CPU core count.</summary>
    [JsonPropertyName("cpu_logical_cores")]
    public int CpuLogicalCores { get; set; }

    /// <summary>Total physical memory in bytes.</summary>
    [JsonPropertyName("physical_memory")]
    public long PhysicalMemory { get; set; }

    /// <summary>Hardware vendor.</summary>
    [JsonPropertyName("hardware_vendor")]
    public string HardwareVendor { get; set; } = "";

    /// <summary>Hardware model.</summary>
    [JsonPropertyName("hardware_model")]
    public string HardwareModel { get; set; } = "";

    /// <summary>Hardware version.</summary>
    [JsonPropertyName("hardware_version")]
    public string HardwareVersion { get; set; } = "";

    /// <summary>Hardware serial number.</summary>
    [JsonPropertyName("hardware_serial")]
    public string HardwareSerial { get; set; } = "";

    /// <summary>System uptime in seconds.</summary>
    [JsonPropertyName("uptime_seconds")]
    public long UptimeSeconds { get; set; }

    /// <summary>BIOS version string.</summary>
    [JsonPropertyName("bios_version")]
    public string BiosVersion { get; set; } = "";

    /// <summary>List of IP addresses.</summary>
    [JsonPropertyName("ip_addresses")]
    public List<string> IpAddresses { get; set; } = [];
}
