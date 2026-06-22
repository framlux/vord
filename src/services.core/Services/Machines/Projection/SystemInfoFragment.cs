// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>SystemInfo-derived summary and detail values.</summary>
/// <param name="Hostname">The reported hostname (summary column).</param>
/// <param name="HardwareModel">The hardware model string (summary column).</param>
/// <param name="IpAddresses">The reported IP addresses as a JSON payload (summary column).</param>
/// <param name="HardwareVendor">The hardware vendor string (detail column).</param>
/// <param name="HardwareSerial">The hardware serial number (detail column).</param>
/// <param name="CpuBrand">The CPU brand string (detail column).</param>
/// <param name="CpuCores">The number of CPU cores (detail column).</param>
/// <param name="MemoryTotalBytes">The total physical memory in bytes (detail column).</param>
/// <param name="UptimeSeconds">The system uptime in seconds (detail column).</param>
/// <param name="BiosVersion">The BIOS version string (detail column).</param>
internal sealed record SystemInfoFragment(
    string? Hostname, string? HardwareModel, string? IpAddresses,
    string? HardwareVendor, string? HardwareSerial, string? CpuBrand,
    int? CpuCores, long? MemoryTotalBytes, long? UptimeSeconds, string? BiosVersion);
