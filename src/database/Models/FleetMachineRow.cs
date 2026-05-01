// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Lightweight projection for fleet overview queries joining Machines with MachineStateSummaries.
/// Does not include JSONB columns to keep queries efficient.
/// </summary>
public sealed class FleetMachineRow
{
    /// <summary>The machine ID.</summary>
    public long Id { get; init; }

    /// <summary>The machine name from the Machine table.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Operating system enum from the Machine table.</summary>
    public OperatingSystems OperatingSystem { get; init; }

    /// <summary>Machine type enum from the Machine table.</summary>
    public MachineTypes MachineType { get; init; }

    /// <summary>Hostname from MachineStateSummary (may be null if no telemetry received).</summary>
    public string? Hostname { get; init; }

    /// <summary>IP addresses JSON string from MachineStateSummary.</summary>
    public string? IpAddresses { get; init; }

    /// <summary>Hardware model from MachineStateSummary.</summary>
    public string? HardwareModel { get; init; }

    /// <summary>CPU usage percentage from MachineStateSummary.</summary>
    public int? CpuUsagePercent { get; init; }

    /// <summary>Memory usage percentage from MachineStateSummary.</summary>
    public int? MemoryUsagePercent { get; init; }

    /// <summary>Pending package updates from MachineStateSummary.</summary>
    public int? PendingUpdates { get; init; }

    /// <summary>Security updates from MachineStateSummary.</summary>
    public int? SecurityUpdates { get; init; }

    /// <summary>Failed services count from MachineStateSummary.</summary>
    public int? FailedServices { get; init; }

    /// <summary>Total services count from MachineStateSummary.</summary>
    public int? TotalServices { get; init; }

    /// <summary>Pre-computed health status (0=healthy, 1=warning, 2=critical, 3=offline).</summary>
    public short HealthStatus { get; init; }

    /// <summary>Last seen timestamp from MachineStateSummary.</summary>
    public DateTimeOffset? LastSeenAt { get; init; }

    /// <summary>Maximum disk usage percentage from MachineStateSummary.</summary>
    public int? MaxDiskUsagePercent { get; init; }

    /// <summary>Whether the machine has a disk health issue.</summary>
    public bool? HasDiskHealthIssue { get; init; }

    /// <summary>Whether the machine has a hardware issue.</summary>
    public bool? HasHardwareIssue { get; init; }

    /// <summary>OS name from MachineStateSummary.</summary>
    public string? OsName { get; init; }

    /// <summary>OS version from MachineStateSummary.</summary>
    public string? OsVersion { get; init; }
}
