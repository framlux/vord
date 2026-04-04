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

    /// <summary>Hostname from MachineState.</summary>
    public string? StateHostname { get; init; }

    /// <summary>IP addresses JSON from MachineState.</summary>
    public string? StateIpAddresses { get; init; }

    /// <summary>Hardware model from MachineState.</summary>
    public string? StateHardwareModel { get; init; }

    /// <summary>CPU usage percentage from MachineState.</summary>
    public int? StateCpuUsagePercent { get; init; }

    /// <summary>Memory usage percentage from MachineState.</summary>
    public int? StateMemoryUsagePercent { get; init; }

    /// <summary>Pending updates count from MachineState.</summary>
    public int? StatePendingUpdates { get; init; }

    /// <summary>Security updates count from MachineState.</summary>
    public int? StateSecurityUpdates { get; init; }

    /// <summary>Failed services count from MachineState.</summary>
    public int? StateFailedServices { get; init; }

    /// <summary>Total services count from MachineState.</summary>
    public int? StateTotalServices { get; init; }

    /// <summary>DiskUsages JSONB from MachineState. Used for JSONB filter expressions on PostgreSQL.</summary>
    public string? StateDiskUsages { get; init; }

    /// <summary>HardwareHealth JSONB from MachineState. Used for JSONB filter expressions on PostgreSQL.</summary>
    public string? StateHardwareHealth { get; init; }

    /// <summary>Pre-computed health status from the database (0=Healthy, 1=Warning, 2=Critical, 3=Offline).</summary>
    public short? StateHealthStatus { get; init; }

    /// <summary>Last ping timestamp from the database, used for SQL-level last-seen filtering.</summary>
    public DateTimeOffset? StateLastPingAt { get; init; }
}
