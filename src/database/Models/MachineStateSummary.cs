// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Slim read cache of the latest known state per machine for fleet-level queries.
/// Row INSERT'd at machine registration; pure UPDATEs thereafter by the streaming worker.
/// HealthStatus is computed by the periodic health sweep service.
/// </summary>
[Table(TableNames.MachineStateSummary)]
public sealed class MachineStateSummary
{
    /// <summary>
    /// The machine this summary belongs to.
    /// </summary>
    [PrimaryKey]
    [Column("MachineId"), NotNull]
    public long MachineId { get; set; }

    /// <summary>
    /// The tenant that owns this machine. Denormalized from Machines for query performance.
    /// </summary>
    [Column("TenantId"), NotNull]
    public int TenantId { get; set; }

    /// <summary>
    /// Machine name from registration. Denormalized from Machines for search.
    /// </summary>
    [Column("Name"), NotNull]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Operating system enum from registration. Denormalized from Machines for filtering.
    /// </summary>
    [Column("OperatingSystem"), NotNull]
    public byte OperatingSystem { get; set; }

    /// <summary>
    /// Machine type enum from registration. Denormalized from Machines for filtering.
    /// </summary>
    [Column("MachineType"), NotNull]
    public byte MachineType { get; set; }

    /// <summary>
    /// Machine hostname from SystemInfo telemetry.
    /// </summary>
    [Column("Hostname"), Nullable]
    public string? Hostname { get; set; }

    /// <summary>
    /// Hardware model from SystemInfo telemetry.
    /// </summary>
    [Column("HardwareModel"), Nullable]
    public string? HardwareModel { get; set; }

    /// <summary>
    /// JSON array of IP addresses from SystemInfo telemetry.
    /// </summary>
    [Column("IpAddresses", DataType = LinqToDB.DataType.BinaryJson), Nullable]
    public string? IpAddresses { get; set; }

    /// <summary>
    /// Operating system name from OsVersion telemetry.
    /// </summary>
    [Column("OsName"), Nullable]
    public string? OsName { get; set; }

    /// <summary>
    /// Operating system version from OsVersion telemetry.
    /// </summary>
    [Column("OsVersion"), Nullable]
    public string? OsVersion { get; set; }

    /// <summary>
    /// CPU usage percentage from CpuUsage telemetry.
    /// </summary>
    [Column("CpuUsagePercent"), Nullable]
    public int? CpuUsagePercent { get; set; }

    /// <summary>
    /// Memory usage percentage from MemoryUsage telemetry.
    /// </summary>
    [Column("MemoryUsagePercent"), Nullable]
    public int? MemoryUsagePercent { get; set; }

    /// <summary>
    /// Maximum disk usage percentage across all disks. Pre-computed from DiskUsages JSONB in C#.
    /// </summary>
    [Column("MaxDiskUsagePercent"), Nullable]
    public int? MaxDiskUsagePercent { get; set; }

    /// <summary>
    /// Total pending package updates from PackageUpdates telemetry.
    /// </summary>
    [Column("PendingUpdates"), Nullable]
    public int? PendingUpdates { get; set; }

    /// <summary>
    /// Security-only package updates from PackageUpdates telemetry.
    /// </summary>
    [Column("SecurityUpdates"), Nullable]
    public int? SecurityUpdates { get; set; }

    /// <summary>
    /// Number of failed systemd services from ServiceStatus telemetry.
    /// </summary>
    [Column("FailedServices"), Nullable]
    public int? FailedServices { get; set; }

    /// <summary>
    /// Total systemd services from ServiceStatus telemetry.
    /// </summary>
    [Column("TotalServices"), Nullable]
    public int? TotalServices { get; set; }

    /// <summary>
    /// Whether any disk has a SMART health failure. Pre-computed from HardwareHealth JSONB in C#.
    /// </summary>
    [Column("HasDiskHealthIssue"), Nullable]
    public bool? HasDiskHealthIssue { get; set; }

    /// <summary>
    /// Whether any hardware component has an issue (fans, power supplies). Pre-computed from HardwareHealth JSONB in C#.
    /// </summary>
    [Column("HasHardwareIssue"), Nullable]
    public bool? HasHardwareIssue { get; set; }

    /// <summary>
    /// Computed health status: 0=Healthy, 1=Warning, 2=Critical, 3=Offline.
    /// Set by the health sweep service.
    /// </summary>
    [Column("HealthStatus"), NotNull]
    public short HealthStatus { get; set; }

    /// <summary>
    /// When the machine was last seen (MAX ReceivedAt across all telemetry types).
    /// Used for online/offline detection by the health sweep.
    /// </summary>
    [Column("LastSeenAt"), Nullable]
    public DateTimeOffset? LastSeenAt { get; set; }
}
