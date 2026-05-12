// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Machines;

/// <summary>
/// Search criteria for the machine search endpoint.
/// </summary>
public sealed class MachineSearchCriteria
{
    /// <summary>Page number (1-based).</summary>
    public int Page { get; set; } = 1;

    /// <summary>Number of items per page.</summary>
    public int PageSize { get; set; } = 25;

    /// <summary>Free-text search across name, hostname, IP, and hardware model.</summary>
    public string? Search { get; set; }

    /// <summary>Comma-separated health statuses to include (healthy, warning, critical, offline).</summary>
    public string? HealthStatus { get; set; }

    /// <summary>Operating system filter (enum name).</summary>
    public string? Os { get; set; }

    /// <summary>Machine type filter (enum name).</summary>
    public string? Type { get; set; }

    /// <summary>Minimum CPU usage percentage (0-100).</summary>
    public int? CpuMin { get; set; }

    /// <summary>Maximum CPU usage percentage (0-100).</summary>
    public int? CpuMax { get; set; }

    /// <summary>Minimum memory usage percentage (0-100).</summary>
    public int? MemoryMin { get; set; }

    /// <summary>Maximum memory usage percentage (0-100).</summary>
    public int? MemoryMax { get; set; }

    /// <summary>Minimum disk usage percentage (0-100).</summary>
    public int? DiskMin { get; set; }

    /// <summary>Maximum disk usage percentage (0-100).</summary>
    public int? DiskMax { get; set; }

    /// <summary>Minimum number of pending updates.</summary>
    public int? PendingUpdatesMin { get; set; }

    /// <summary>Minimum number of security updates.</summary>
    public int? SecurityUpdatesMin { get; set; }

    /// <summary>Minimum number of failed services.</summary>
    public int? FailedServicesMin { get; set; }

    /// <summary>Filter for machines with disk health issues only.</summary>
    public bool? HasDiskHealthIssue { get; set; }

    /// <summary>Filter for machines with hardware issues only.</summary>
    public bool? HasHardwareIssue { get; set; }

    /// <summary>Only include machines seen after this timestamp (ISO8601).</summary>
    public DateTimeOffset? LastSeenAfter { get; set; }

    /// <summary>Only include machines seen before this timestamp (ISO8601).</summary>
    public DateTimeOffset? LastSeenBefore { get; set; }

    /// <summary>Sort field (name, status, cpu, memory, disk).</summary>
    public string SortBy { get; set; } = "name";

    /// <summary>Sort direction (asc, desc).</summary>
    public string SortDir { get; set; } = "asc";
}
