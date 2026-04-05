// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Lightweight projection for search queries including OS and type from the Machine table.
/// </summary>
internal sealed class SearchScalarRow
{
    /// <summary>Machine database ID.</summary>
    public long Id { get; init; }

    /// <summary>Machine display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Operating system enum value.</summary>
    public OperatingSystems OperatingSystem { get; init; }

    /// <summary>Machine type enum value.</summary>
    public MachineTypes MachineType { get; init; }

    /// <summary>Hostname from MachineStateSummary.</summary>
    public string? StateHostname { get; init; }

    /// <summary>IP addresses JSON from MachineStateSummary.</summary>
    public string? StateIpAddresses { get; init; }

    /// <summary>Hardware model from MachineStateSummary.</summary>
    public string? StateHardwareModel { get; init; }

    /// <summary>CPU usage percentage from MachineStateSummary.</summary>
    public int? StateCpuUsagePercent { get; init; }

    /// <summary>Memory usage percentage from MachineStateSummary.</summary>
    public int? StateMemoryUsagePercent { get; init; }

    /// <summary>Pending updates count from MachineStateSummary.</summary>
    public int? StatePendingUpdates { get; init; }

    /// <summary>Security updates count from MachineStateSummary.</summary>
    public int? StateSecurityUpdates { get; init; }

    /// <summary>Failed services count from MachineStateSummary.</summary>
    public int? StateFailedServices { get; init; }

    /// <summary>Total services count from MachineStateSummary.</summary>
    public int? StateTotalServices { get; init; }

    /// <summary>Maximum disk usage percentage from MachineStateSummary.</summary>
    public int? StateMaxDiskUsagePercent { get; init; }

    /// <summary>Whether any disk has a SMART health failure from MachineStateSummary.</summary>
    public bool? StateHasDiskHealthIssue { get; init; }

    /// <summary>Whether any hardware component has an issue from MachineStateSummary.</summary>
    public bool? StateHasHardwareIssue { get; init; }

    /// <summary>Pre-computed health status from the database (0=Healthy, 1=Warning, 2=Critical, 3=Offline).</summary>
    public short? StateHealthStatus { get; init; }

    /// <summary>Last ping timestamp from the database, used for SQL-level last-seen filtering.</summary>
    public DateTimeOffset? StateLastPingAt { get; init; }
}
