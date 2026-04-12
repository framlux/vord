// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Cold detail table storing full telemetry payloads per machine.
/// Only read when viewing a single machine's detail page.
/// Row INSERT'd at machine registration; pure UPDATEs thereafter by the streaming worker.
/// </summary>
[Table(TableNames.MachineStateDetail)]
public sealed class MachineStateDetail
{
    /// <summary>
    /// The machine this detail belongs to.
    /// </summary>
    [PrimaryKey]
    [Column("MachineId"), NotNull]
    public long MachineId { get; set; }

    /// <summary>
    /// Hardware vendor from SystemInfo telemetry.
    /// </summary>
    [Column("HardwareVendor"), Nullable]
    public string? HardwareVendor { get; set; }

    /// <summary>
    /// Hardware serial number from SystemInfo telemetry.
    /// </summary>
    [Column("HardwareSerial"), Nullable]
    public string? HardwareSerial { get; set; }

    /// <summary>
    /// CPU brand string from SystemInfo telemetry.
    /// </summary>
    [Column("CpuBrand"), Nullable]
    public string? CpuBrand { get; set; }

    /// <summary>
    /// Physical CPU core count from SystemInfo telemetry.
    /// </summary>
    [Column("CpuCores"), Nullable]
    public int? CpuCores { get; set; }

    /// <summary>
    /// Total physical memory in bytes from SystemInfo telemetry.
    /// </summary>
    [Column("MemoryTotalBytes"), Nullable]
    public long? MemoryTotalBytes { get; set; }

    /// <summary>
    /// System uptime in seconds from SystemInfo telemetry.
    /// </summary>
    [Column("UptimeSeconds"), Nullable]
    public long? UptimeSeconds { get; set; }

    /// <summary>
    /// BIOS version string from SystemInfo telemetry.
    /// </summary>
    [Column("BiosVersion"), Nullable]
    public string? BiosVersion { get; set; }

    /// <summary>
    /// Kernel version string from OsVersion telemetry.
    /// </summary>
    [Column("Kernel"), Nullable]
    public string? Kernel { get; set; }

    /// <summary>
    /// CPU architecture type from CpuInfo telemetry.
    /// </summary>
    [Column("CpuType"), Nullable]
    public string? CpuType { get; set; }

    /// <summary>
    /// Physical CPU count from CpuInfo telemetry.
    /// </summary>
    [Column("CpuPhysicalCpus"), Nullable]
    public int? CpuPhysicalCpus { get; set; }

    /// <summary>
    /// Logical CPU count from CpuInfo telemetry.
    /// </summary>
    [Column("CpuLogicalCpus"), Nullable]
    public int? CpuLogicalCpus { get; set; }

    /// <summary>
    /// Total swap space in bytes from MemoryInfo telemetry.
    /// </summary>
    [Column("SwapTotalBytes"), Nullable]
    public long? SwapTotalBytes { get; set; }

    /// <summary>
    /// Free swap space in bytes from MemoryInfo telemetry.
    /// </summary>
    [Column("SwapFreeBytes"), Nullable]
    public long? SwapFreeBytes { get; set; }

    /// <summary>
    /// Used memory in bytes from MemoryUsage telemetry.
    /// </summary>
    [Column("MemoryUsedBytes"), Nullable]
    public long? MemoryUsedBytes { get; set; }

    /// <summary>
    /// JSONB array of disk info entries from DiskInfo telemetry.
    /// </summary>
    [Column("DiskInfos", DataType = LinqToDB.DataType.BinaryJson), Nullable]
    public string? DiskInfos { get; set; }

    /// <summary>
    /// JSONB array of disk usage entries from DiskUsage telemetry.
    /// </summary>
    [Column("DiskUsages", DataType = LinqToDB.DataType.BinaryJson), Nullable]
    public string? DiskUsages { get; set; }

    /// <summary>
    /// JSONB array of SSH session events.
    /// </summary>
    [Column("SshSessions", DataType = LinqToDB.DataType.BinaryJson), Nullable]
    public string? SshSessions { get; set; }

    /// <summary>
    /// JSONB object with structured hardware health from HardwareHealth telemetry.
    /// </summary>
    [Column("HardwareHealth", DataType = LinqToDB.DataType.BinaryJson), Nullable]
    public string? HardwareHealth { get; set; }
}
