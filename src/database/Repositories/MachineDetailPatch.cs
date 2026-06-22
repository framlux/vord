// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Database-layer carrier for a combined machine state detail update. Holds the plain nullable
/// column values plus a per-owning-type presence flag so the apply method can set only the columns
/// owned by the telemetry types present in the collapsed batch. The <see cref="HasAnyDetail"/> flag
/// lets the caller skip an update that would set zero columns.
/// This type lives in the database project so the dependency arrow (services.core to database) is
/// preserved: callers map their projection patch onto this carrier before crossing the boundary.
/// </summary>
public sealed record MachineDetailPatch
{
    /// <summary>The machine the patch targets.</summary>
    public required long MachineId { get; init; }

    /// <summary>True when the SystemInfo-owned detail columns should be written.</summary>
    public bool HasSystemInfo { get; init; }

    /// <summary>Hardware vendor from SystemInfo telemetry.</summary>
    public string? HardwareVendor { get; init; }

    /// <summary>Hardware serial number from SystemInfo telemetry.</summary>
    public string? HardwareSerial { get; init; }

    /// <summary>CPU brand string from SystemInfo telemetry.</summary>
    public string? CpuBrand { get; init; }

    /// <summary>Physical CPU core count from SystemInfo telemetry.</summary>
    public int? CpuCores { get; init; }

    /// <summary>Total physical memory in bytes from SystemInfo telemetry.</summary>
    public long? MemoryTotalBytes { get; init; }

    /// <summary>System uptime in seconds from SystemInfo telemetry.</summary>
    public long? UptimeSeconds { get; init; }

    /// <summary>BIOS version string from SystemInfo telemetry.</summary>
    public string? BiosVersion { get; init; }

    /// <summary>True when the OsVersion-owned detail column should be written.</summary>
    public bool HasOsVersion { get; init; }

    /// <summary>Kernel version string from OsVersion telemetry.</summary>
    public string? Kernel { get; init; }

    /// <summary>True when the CpuInfo-owned detail columns should be written.</summary>
    public bool HasCpuInfo { get; init; }

    /// <summary>CPU architecture type from CpuInfo telemetry.</summary>
    public string? CpuType { get; init; }

    /// <summary>Physical CPU count from CpuInfo telemetry.</summary>
    public int? CpuPhysicalCpus { get; init; }

    /// <summary>Logical CPU count from CpuInfo telemetry.</summary>
    public int? CpuLogicalCpus { get; init; }

    /// <summary>True when the MemoryInfo-owned detail columns should be written.</summary>
    public bool HasMemoryInfo { get; init; }

    /// <summary>Total swap space in bytes from MemoryInfo telemetry.</summary>
    public long? SwapTotalBytes { get; init; }

    /// <summary>Free swap space in bytes from MemoryInfo telemetry.</summary>
    public long? SwapFreeBytes { get; init; }

    /// <summary>True when the MemoryUsage-owned detail column should be written.</summary>
    public bool HasMemoryUsage { get; init; }

    /// <summary>Used memory in bytes from MemoryUsage telemetry.</summary>
    public long? MemoryUsedBytes { get; init; }

    /// <summary>True when the DiskInfo-owned detail column should be written.</summary>
    public bool HasDiskInfo { get; init; }

    /// <summary>JSONB array of disk info entries from DiskInfo telemetry.</summary>
    public string? DiskInfos { get; init; }

    /// <summary>True when the DiskUsage-owned detail column should be written.</summary>
    public bool HasDiskUsage { get; init; }

    /// <summary>JSONB array of disk usage entries from DiskUsage telemetry.</summary>
    public string? DiskUsages { get; init; }

    /// <summary>True when the SshSessions-owned detail column should be written.</summary>
    public bool HasSshSessions { get; init; }

    /// <summary>JSONB array of SSH session events from SshSessions telemetry.</summary>
    public string? SshSessions { get; init; }

    /// <summary>True when the HardwareHealth-owned detail column should be written.</summary>
    public bool HasHardwareHealth { get; init; }

    /// <summary>JSONB object with structured hardware health from HardwareHealth telemetry.</summary>
    public string? HardwareHealth { get; init; }

    /// <summary>
    /// True when at least one detail-bearing telemetry type is present. The caller uses this to
    /// skip an update that would set zero columns.
    /// </summary>
    public bool HasAnyDetail =>
        (HasSystemInfo == true) || (HasOsVersion == true) || (HasCpuInfo == true) ||
        (HasMemoryInfo == true) || (HasMemoryUsage == true) || (HasDiskInfo == true) ||
        (HasDiskUsage == true) || (HasSshSessions == true) || (HasHardwareHealth == true);
}
