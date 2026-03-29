// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;

/// <summary>
/// Single machine row in the fleet table.
/// </summary>
public sealed class FleetMachineDto
{
    /// <summary>Machine database ID.</summary>
    public long Id { get; set; }

    /// <summary>Machine display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Hostname from telemetry.</summary>
    public string? Hostname { get; set; }

    /// <summary>First reported IP address.</summary>
    public string? IpAddress { get; set; }

    /// <summary>Hardware model string.</summary>
    public string? HardwareModel { get; set; }

    /// <summary>Computed health status.</summary>
    public MachineHealthStatus HealthStatus { get; set; }

    /// <summary>Latest CPU usage percentage.</summary>
    public int? CpuUsagePercent { get; set; }

    /// <summary>Latest memory usage percentage.</summary>
    public int? MemoryUsagePercent { get; set; }

    /// <summary>Highest disk usage percentage across all mounts.</summary>
    public int? MaxDiskUsagePercent { get; set; }

    /// <summary>Whether any disk has a SMART health issue.</summary>
    public bool HasDiskHealthIssue { get; set; }

    /// <summary>Whether any hardware sensor is in a bad state.</summary>
    public bool HasHardwareIssue { get; set; }

    /// <summary>Whether the machine is considered online.</summary>
    public bool IsOnline { get; set; }

    /// <summary>Last ping timestamp.</summary>
    public DateTimeOffset? LastPing { get; set; }

    /// <summary>Total pending package updates.</summary>
    public int PendingUpdates { get; set; }

    /// <summary>Pending security updates.</summary>
    public int SecurityUpdates { get; set; }

    /// <summary>Number of failed services.</summary>
    public int FailedServices { get; set; }

    /// <summary>Total services.</summary>
    public int TotalServices { get; set; }
}
