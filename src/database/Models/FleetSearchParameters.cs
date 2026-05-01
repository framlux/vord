// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Parameters for searching fleet machines with SQL-level filtering, sorting, and pagination.
/// </summary>
public sealed class FleetSearchParameters
{
    /// <summary>Text search against name, hostname, and hardware model.</summary>
    public string? Search { get; init; }

    /// <summary>Filter by operating system type.</summary>
    public OperatingSystems? Os { get; init; }

    /// <summary>Filter by machine type.</summary>
    public MachineTypes? MachineType { get; init; }

    /// <summary>Minimum CPU usage percent filter.</summary>
    public int? CpuMin { get; init; }

    /// <summary>Maximum CPU usage percent filter.</summary>
    public int? CpuMax { get; init; }

    /// <summary>Minimum memory usage percent filter.</summary>
    public int? MemoryMin { get; init; }

    /// <summary>Maximum memory usage percent filter.</summary>
    public int? MemoryMax { get; init; }

    /// <summary>Minimum pending updates filter.</summary>
    public int? PendingUpdatesMin { get; init; }

    /// <summary>Minimum security updates filter.</summary>
    public int? SecurityUpdatesMin { get; init; }

    /// <summary>Minimum failed services filter.</summary>
    public int? FailedServicesMin { get; init; }

    /// <summary>Minimum disk usage percent filter.</summary>
    public int? DiskMin { get; init; }

    /// <summary>Maximum disk usage percent filter.</summary>
    public int? DiskMax { get; init; }

    /// <summary>Filter by disk health issue presence.</summary>
    public bool? HasDiskHealthIssue { get; init; }

    /// <summary>Filter by hardware issue presence.</summary>
    public bool? HasHardwareIssue { get; init; }

    /// <summary>Filter by pre-computed health status values.</summary>
    public List<short>? HealthStatusValues { get; init; }

    /// <summary>Filter by last seen after this time.</summary>
    public DateTimeOffset? LastSeenAfter { get; init; }

    /// <summary>Filter by last seen before this time.</summary>
    public DateTimeOffset? LastSeenBefore { get; init; }

    /// <summary>Sort field name.</summary>
    public string? SortBy { get; init; }

    /// <summary>Sort descending when true.</summary>
    public bool SortDescending { get; init; }

    /// <summary>Number of rows to skip.</summary>
    public int Skip { get; init; }

    /// <summary>Number of rows to return.</summary>
    public int Take { get; init; }
}
