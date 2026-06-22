// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines.Projection;

/// <summary>
/// A single machine's collapsed projection update for one telemetry batch.
/// Each non-null fragment carries the columns of the latest row of that type in the batch.
/// </summary>
internal sealed record MachineStatePatch
{
    /// <summary>The machine the patch targets.</summary>
    public required long MachineId { get; init; }

    /// <summary>MAX(ReceivedAt) across all batch rows for this machine; applied with a monotonic guard.</summary>
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>SystemInfo fragment, or null when no SystemInfo row was present.</summary>
    public SystemInfoFragment? SystemInfo { get; init; }

    /// <summary>OsVersion fragment, or null when absent.</summary>
    public OsVersionFragment? OsVersion { get; init; }

    /// <summary>CpuInfo fragment, or null when absent.</summary>
    public CpuInfoFragment? CpuInfo { get; init; }

    /// <summary>MemoryInfo fragment, or null when absent.</summary>
    public MemoryInfoFragment? MemoryInfo { get; init; }

    /// <summary>DiskInfo fragment, or null when absent.</summary>
    public DiskInfoFragment? DiskInfo { get; init; }

    /// <summary>CpuUsage fragment, or null when absent.</summary>
    public CpuUsageFragment? CpuUsage { get; init; }

    /// <summary>MemoryUsage fragment, or null when absent.</summary>
    public MemoryUsageFragment? MemoryUsage { get; init; }

    /// <summary>DiskUsage fragment, or null when absent.</summary>
    public DiskUsageFragment? DiskUsage { get; init; }

    /// <summary>SshSessions fragment, or null when absent.</summary>
    public SshSessionsFragment? SshSessions { get; init; }

    /// <summary>HardwareHealth fragment, or null when absent.</summary>
    public HardwareHealthFragment? HardwareHealth { get; init; }

    /// <summary>PackageUpdates fragment, or null when absent.</summary>
    public PackageUpdatesFragment? PackageUpdates { get; init; }

    /// <summary>ServiceStatus fragment, or null when absent.</summary>
    public ServiceStatusFragment? ServiceStatus { get; init; }

    /// <summary>True when at least one detail-bearing telemetry type is present.</summary>
    public bool HasDetailChanges =>
        (SystemInfo is not null) || (OsVersion is not null) || (CpuInfo is not null) ||
        (MemoryInfo is not null) || (DiskInfo is not null) || (MemoryUsage is not null) ||
        (DiskUsage is not null) || (SshSessions is not null) || (HardwareHealth is not null);
}
