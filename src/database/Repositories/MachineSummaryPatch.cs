// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Database-layer carrier for a combined machine state summary update. Holds the plain
/// nullable column values plus a per-owning-type presence flag so the apply method can set
/// only the columns owned by the telemetry types present in the collapsed batch.
/// This type lives in the database project so the dependency arrow (services.core to database)
/// is preserved: callers map their projection patch onto this carrier before crossing the boundary.
/// </summary>
public sealed record MachineSummaryPatch
{
    /// <summary>The machine the patch targets.</summary>
    public required long MachineId { get; init; }

    /// <summary>
    /// MAX(ReceivedAt) across all batch rows for this machine. Applied with a monotonic guard
    /// so an already-stored value is never moved backward.
    /// </summary>
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>True when the SystemInfo-owned summary columns should be written.</summary>
    public bool HasSystemInfo { get; init; }

    /// <summary>Machine hostname from SystemInfo telemetry.</summary>
    public string? Hostname { get; init; }

    /// <summary>Hardware model from SystemInfo telemetry.</summary>
    public string? HardwareModel { get; init; }

    /// <summary>JSON array of IP addresses from SystemInfo telemetry.</summary>
    public string? IpAddresses { get; init; }

    /// <summary>True when the OsVersion-owned summary columns should be written.</summary>
    public bool HasOsVersion { get; init; }

    /// <summary>Operating system name from OsVersion telemetry.</summary>
    public string? OsName { get; init; }

    /// <summary>Operating system version from OsVersion telemetry.</summary>
    public string? OsVersion { get; init; }

    /// <summary>True when the CpuUsage-owned summary column should be written.</summary>
    public bool HasCpuUsage { get; init; }

    /// <summary>CPU usage percentage from CpuUsage telemetry.</summary>
    public int? CpuUsagePercent { get; init; }

    /// <summary>True when the MemoryUsage-owned summary column should be written.</summary>
    public bool HasMemoryUsage { get; init; }

    /// <summary>Memory usage percentage from MemoryUsage telemetry.</summary>
    public int? MemoryUsagePercent { get; init; }

    /// <summary>True when the DiskUsage-owned summary column should be written.</summary>
    public bool HasDiskUsage { get; init; }

    /// <summary>Maximum disk usage percentage across all disks from DiskUsage telemetry.</summary>
    public int? MaxDiskUsagePercent { get; init; }

    /// <summary>True when the HardwareHealth-owned summary columns should be written.</summary>
    public bool HasHardwareHealth { get; init; }

    /// <summary>Whether any disk has a SMART health failure from HardwareHealth telemetry.</summary>
    public bool? HasDiskHealthIssue { get; init; }

    /// <summary>Whether any hardware component has an issue from HardwareHealth telemetry.</summary>
    public bool? HasHardwareIssue { get; init; }

    /// <summary>True when the PackageUpdates-owned summary columns should be written.</summary>
    public bool HasPackageUpdates { get; init; }

    /// <summary>Total pending package updates from PackageUpdates telemetry.</summary>
    public int? PendingUpdates { get; init; }

    /// <summary>Security-only package updates from PackageUpdates telemetry.</summary>
    public int? SecurityUpdates { get; init; }

    /// <summary>True when the ServiceStatus-owned summary columns should be written.</summary>
    public bool HasServiceStatus { get; init; }

    /// <summary>Total systemd services from ServiceStatus telemetry.</summary>
    public int? TotalServices { get; init; }

    /// <summary>Number of failed systemd services from ServiceStatus telemetry.</summary>
    public int? FailedServices { get; init; }
}
