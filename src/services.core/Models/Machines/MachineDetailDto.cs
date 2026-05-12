// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Models.Telemetry;

namespace Framlux.FleetManagement.Services.Core.Models.Machines;

/// <summary>
/// Full detail view for a single machine's slide-over panel.
/// </summary>
public sealed class MachineDetailDto
{
    /// <summary>Machine database ID.</summary>
    public long Id { get; set; }

    /// <summary>Machine display name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Hostname from telemetry.</summary>
    public string? Hostname { get; set; }

    /// <summary>Whether the machine is considered online.</summary>
    public bool IsOnline { get; set; }

    /// <summary>Last ping timestamp.</summary>
    public DateTimeOffset? LastPing { get; set; }

    /// <summary>Computed health status.</summary>
    public MachineHealthStatus HealthStatus { get; set; }

    /// <summary>Latest SystemInfo payload.</summary>
    public SystemInfoPayload? SystemInfo { get; set; }

    /// <summary>Latest OsVersion payload.</summary>
    public OsVersionPayload? OsVersion { get; set; }

    /// <summary>Latest CPU usage payload.</summary>
    public CpuUsagePayload? CpuUsage { get; set; }

    /// <summary>Latest memory usage payload.</summary>
    public MemoryUsagePayload? MemoryUsage { get; set; }

    /// <summary>Latest disk usage payload (array of all mounts).</summary>
    public DiskUsagePayload? DiskUsages { get; set; }

    /// <summary>Latest hardware health payload.</summary>
    public HardwareHealthPayload? HardwareHealth { get; set; }

    /// <summary>Latest package updates payload.</summary>
    public PackageUpdatesPayload? PackageUpdates { get; set; }

    /// <summary>List of failed services.</summary>
    public List<ServiceEntryDto> FailedServices { get; set; } = [];

    /// <summary>Total service count.</summary>
    public int TotalServices { get; set; }

    /// <summary>Recent SSH sessions.</summary>
    public List<SshSessionPayload> RecentSshSessions { get; set; } = [];

    /// <summary>Most recent telemetry timestamp across all types.</summary>
    public DateTimeOffset? TelemetryLastUpdated { get; set; }
}
