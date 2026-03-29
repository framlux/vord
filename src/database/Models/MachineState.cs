// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Denormalized cache of the latest known state per machine, updated periodically.
/// </summary>
[Table(TableNames.MachineState)]
public sealed class MachineState
{
    /// <summary>
    /// The machine this state belongs to.
    /// </summary>
    [PrimaryKey]
    [Column("MachineId"), NotNull]
    public long MachineId { get; set; }

    /// <summary>
    /// Machine hostname from SystemInfo.
    /// </summary>
    [Column("Hostname"), Nullable]
    public string? Hostname { get; set; }

    /// <summary>
    /// Hardware vendor from SystemInfo.
    /// </summary>
    [Column("HardwareVendor"), Nullable]
    public string? HardwareVendor { get; set; }

    /// <summary>
    /// Hardware model from SystemInfo.
    /// </summary>
    [Column("HardwareModel"), Nullable]
    public string? HardwareModel { get; set; }

    /// <summary>
    /// Hardware serial number from SystemInfo.
    /// </summary>
    [Column("HardwareSerial"), Nullable]
    public string? HardwareSerial { get; set; }

    /// <summary>
    /// CPU brand string from SystemInfo.
    /// </summary>
    [Column("CpuBrand"), Nullable]
    public string? CpuBrand { get; set; }

    /// <summary>
    /// Physical CPU core count from SystemInfo.
    /// </summary>
    [Column("CpuCores"), Nullable]
    public int? CpuCores { get; set; }

    /// <summary>
    /// Total physical memory in bytes from SystemInfo.
    /// </summary>
    [Column("MemoryTotalBytes"), Nullable]
    public long? MemoryTotalBytes { get; set; }

    /// <summary>
    /// System uptime in seconds from SystemInfo.
    /// </summary>
    [Column("UptimeSeconds"), Nullable]
    public long? UptimeSeconds { get; set; }

    /// <summary>
    /// BIOS version string from SystemInfo.
    /// </summary>
    [Column("BiosVersion"), Nullable]
    public string? BiosVersion { get; set; }

    /// <summary>
    /// JSON array of IP addresses from SystemInfo.
    /// </summary>
    [Column("IpAddresses"), Nullable]
    public string? IpAddresses { get; set; }

    /// <summary>
    /// Operating system name from OsVersion.
    /// </summary>
    [Column("OsName"), Nullable]
    public string? OsName { get; set; }

    /// <summary>
    /// Operating system version from OsVersion.
    /// </summary>
    [Column("OsVersion"), Nullable]
    public string? OsVersion { get; set; }

    /// <summary>
    /// Kernel version string from OsVersion.
    /// </summary>
    [Column("Kernel"), Nullable]
    public string? Kernel { get; set; }

    /// <summary>
    /// CPU usage percentage from CpuUtilization.
    /// </summary>
    [Column("CpuUsagePercent"), Nullable]
    public int? CpuUsagePercent { get; set; }

    /// <summary>
    /// Used memory in bytes from MemoryUtilization.
    /// </summary>
    [Column("MemoryUsedBytes"), Nullable]
    public long? MemoryUsedBytes { get; set; }

    /// <summary>
    /// Memory usage percentage from MemoryUtilization.
    /// </summary>
    [Column("MemoryUsagePercent"), Nullable]
    public int? MemoryUsagePercent { get; set; }

    /// <summary>
    /// JSONB array of disk usage entries from DiskUtilization.
    /// </summary>
    [Column("DiskUsages"), Nullable]
    public string? DiskUsages { get; set; }

    /// <summary>
    /// JSONB object with structured hardware health from HardwareHealth.
    /// </summary>
    [Column("HardwareHealth"), Nullable]
    public string? HardwareHealth { get; set; }

    /// <summary>
    /// CPU architecture type from CpuInfo.
    /// </summary>
    [Column("CpuType"), Nullable]
    public string? CpuType { get; set; }

    /// <summary>
    /// Physical CPU count from CpuInfo.
    /// </summary>
    [Column("CpuPhysicalCpus"), Nullable]
    public int? CpuPhysicalCpus { get; set; }

    /// <summary>
    /// Logical CPU count from CpuInfo.
    /// </summary>
    [Column("CpuLogicalCpus"), Nullable]
    public int? CpuLogicalCpus { get; set; }

    /// <summary>
    /// Total swap space in bytes from MemoryInfo.
    /// </summary>
    [Column("SwapTotalBytes"), Nullable]
    public long? SwapTotalBytes { get; set; }

    /// <summary>
    /// Free swap space in bytes from MemoryInfo.
    /// </summary>
    [Column("SwapFreeBytes"), Nullable]
    public long? SwapFreeBytes { get; set; }

    /// <summary>
    /// JSONB array of disk info entries from DiskInfo.
    /// </summary>
    [Column("DiskInfos"), Nullable]
    public string? DiskInfos { get; set; }

    /// <summary>
    /// JSONB array of SSH session events.
    /// </summary>
    [Column("SshSessions"), Nullable]
    public string? SshSessions { get; set; }

    /// <summary>
    /// Total pending package updates from PackageUpdates.
    /// </summary>
    [Column("PendingUpdates"), Nullable]
    public int? PendingUpdates { get; set; }

    /// <summary>
    /// Security-only package updates from PackageUpdates.
    /// </summary>
    [Column("SecurityUpdates"), Nullable]
    public int? SecurityUpdates { get; set; }

    /// <summary>
    /// Total systemd services from ServiceStatus.
    /// </summary>
    [Column("TotalServices"), Nullable]
    public int? TotalServices { get; set; }

    /// <summary>
    /// Number of failed systemd services from ServiceStatus.
    /// </summary>
    [Column("FailedServices"), Nullable]
    public int? FailedServices { get; set; }

    /// <summary>
    /// Computed health status: 0=Healthy, 1=Warning, 2=Critical, 3=Offline.
    /// </summary>
    [Column("HealthStatus"), NotNull]
    public short HealthStatus { get; set; }

    /// <summary>
    /// When SystemInfo was last received.
    /// </summary>
    [Column("SystemInfoAt"), Nullable]
    public DateTimeOffset? SystemInfoAt { get; set; }

    /// <summary>
    /// When OsVersion was last received.
    /// </summary>
    [Column("OsVersionAt"), Nullable]
    public DateTimeOffset? OsVersionAt { get; set; }

    /// <summary>
    /// When CpuUtilization was last received.
    /// </summary>
    [Column("CpuUsageAt"), Nullable]
    public DateTimeOffset? CpuUsageAt { get; set; }

    /// <summary>
    /// When MemoryUtilization was last received.
    /// </summary>
    [Column("MemoryUsageAt"), Nullable]
    public DateTimeOffset? MemoryUsageAt { get; set; }

    /// <summary>
    /// When DiskUtilization was last received.
    /// </summary>
    [Column("DiskUsageAt"), Nullable]
    public DateTimeOffset? DiskUsageAt { get; set; }

    /// <summary>
    /// When HardwareHealth was last received.
    /// </summary>
    [Column("HardwareHealthAt"), Nullable]
    public DateTimeOffset? HardwareHealthAt { get; set; }

    /// <summary>
    /// When PackageUpdates was last received.
    /// </summary>
    [Column("PackageUpdatesAt"), Nullable]
    public DateTimeOffset? PackageUpdatesAt { get; set; }

    /// <summary>
    /// When ServiceStatus was last received.
    /// </summary>
    [Column("ServiceStatusAt"), Nullable]
    public DateTimeOffset? ServiceStatusAt { get; set; }

    /// <summary>
    /// When CpuInfo was last received.
    /// </summary>
    [Column("CpuInfoAt"), Nullable]
    public DateTimeOffset? CpuInfoAt { get; set; }

    /// <summary>
    /// When MemoryInfo was last received.
    /// </summary>
    [Column("MemoryInfoAt"), Nullable]
    public DateTimeOffset? MemoryInfoAt { get; set; }

    /// <summary>
    /// When DiskInfo was last received.
    /// </summary>
    [Column("DiskInfoAt"), Nullable]
    public DateTimeOffset? DiskInfoAt { get; set; }

    /// <summary>
    /// When SSH sessions were last received.
    /// </summary>
    [Column("SshSessionsAt"), Nullable]
    public DateTimeOffset? SshSessionsAt { get; set; }

    /// <summary>
    /// The most recent telemetry timestamp across all types.
    /// </summary>
    [Column("LastTelemetryAt"), Nullable]
    public DateTimeOffset? LastTelemetryAt { get; set; }
}
