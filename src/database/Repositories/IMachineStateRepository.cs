// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for machine state summary and detail operations.
/// </summary>
public interface IMachineStateRepository
{
    /// <summary>
    /// Inserts a new machine state summary row. Used during machine registration.
    /// </summary>
    /// <param name="summary">The summary row to insert.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task InsertSummaryAsync(MachineStateSummary summary, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new machine state detail row. Used during machine registration.
    /// </summary>
    /// <param name="detail">The detail row to insert.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task InsertDetailAsync(MachineStateDetail detail, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk-inserts telemetry rows using the most efficient copy strategy.
    /// </summary>
    /// <param name="rows">The telemetry rows to insert.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task BulkInsertTelemetryAsync(List<MachineTelemetry> rows, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a single telemetry row.
    /// </summary>
    /// <param name="row">The telemetry row to insert.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task InsertTelemetryAsync(MachineTelemetry row, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the distinct tenant IDs that have at least one machine state summary row.
    /// Used by the health sweep service to partition work by tenant.
    /// </summary>
    Task<List<int>> GetDistinctTenantIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a raw SQL health sweep for a single tenant.
    /// The caller provides the dialect-specific SQL string.
    /// </summary>
    /// <param name="sql">Dialect-specific SQL for the health sweep.</param>
    /// <param name="tenantId">The tenant to sweep.</param>
    /// <param name="onlineThresholdSeconds">Seconds before a machine is considered offline.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of rows affected.</returns>
    Task<int> SweepHealthStatusAsync(string sql, int tenantId, int onlineThresholdSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the state summary for a single machine.
    /// </summary>
    Task<MachineStateSummary?> GetSummaryForMachineAsync(long machineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all state summaries for machines belonging to a tenant that are not deleted.
    /// Used by the alert evaluation service.
    /// </summary>
    Task<List<MachineStateSummary>> GetSummariesForTenantMachinesAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a dictionary mapping machine IDs to their hostnames from state summaries.
    /// </summary>
    Task<Dictionary<long, string?>> GetHostnameMapAsync(List<long> machineIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a dictionary mapping machine IDs to their display names from state summaries.
    /// </summary>
    Task<Dictionary<long, string>> GetNameMapAsync(List<long> machineIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns state summaries keyed by machine ID for the specified machine IDs.
    /// </summary>
    Task<Dictionary<long, MachineStateSummary>> GetSummariesByMachineIdsAsync(List<long> machineIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns state summaries for the specified machine IDs as a list.
    /// </summary>
    Task<List<MachineStateSummary>> GetSummaryListByMachineIdsAsync(List<long> machineIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the Name column on the machine state summary for a given machine.
    /// </summary>
    Task UpdateSummaryNameAsync(long machineId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the summary fields from SystemInfo telemetry.
    /// </summary>
    Task UpdateSystemInfoSummaryAsync(long machineId, string? hostname, string? hardwareModel, string? ipAddresses, DateTimeOffset lastSeenAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the detail fields from SystemInfo telemetry.
    /// </summary>
    Task UpdateSystemInfoDetailAsync(long machineId, string? hardwareVendor, string? hardwareSerial, string? cpuBrand, int? cpuCores, long? memoryTotalBytes, long? uptimeSeconds, string? biosVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the summary fields from OsVersion telemetry.
    /// </summary>
    Task UpdateOsVersionSummaryAsync(long machineId, string? osName, string? osVersion, DateTimeOffset lastSeenAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the detail fields from OsVersion telemetry.
    /// </summary>
    Task UpdateOsVersionDetailAsync(long machineId, string? kernel, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the summary LastSeenAt from CpuInfo telemetry.
    /// </summary>
    Task UpdateCpuInfoSummaryAsync(long machineId, DateTimeOffset lastSeenAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the detail fields from CpuInfo telemetry.
    /// </summary>
    Task UpdateCpuInfoDetailAsync(long machineId, string? cpuType, int? physicalCpus, int? logicalCpus, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the summary LastSeenAt from MemoryInfo telemetry.
    /// </summary>
    Task UpdateMemoryInfoSummaryAsync(long machineId, DateTimeOffset lastSeenAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the detail fields from MemoryInfo telemetry.
    /// </summary>
    Task UpdateMemoryInfoDetailAsync(long machineId, long? swapTotalBytes, long? swapFreeBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the summary LastSeenAt from DiskInfo telemetry.
    /// </summary>
    Task UpdateDiskInfoSummaryAsync(long machineId, DateTimeOffset lastSeenAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the detail DiskInfos payload from DiskInfo telemetry.
    /// </summary>
    Task UpdateDiskInfoDetailAsync(long machineId, string payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the summary fields from CpuUsage telemetry.
    /// </summary>
    Task UpdateCpuUsageSummaryAsync(long machineId, int? cpuUsagePercent, DateTimeOffset lastSeenAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the summary fields from MemoryUsage telemetry.
    /// </summary>
    Task UpdateMemoryUsageSummaryAsync(long machineId, int? memoryUsagePercent, DateTimeOffset lastSeenAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the detail MemoryUsedBytes from MemoryUsage telemetry.
    /// </summary>
    Task UpdateMemoryUsageDetailAsync(long machineId, long? memoryUsedBytes, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the summary fields from DiskUsage telemetry.
    /// </summary>
    Task UpdateDiskUsageSummaryAsync(long machineId, int maxDiskUsagePercent, DateTimeOffset lastSeenAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the detail DiskUsages payload from DiskUsage telemetry.
    /// </summary>
    Task UpdateDiskUsageDetailAsync(long machineId, string payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the summary LastSeenAt from SshSessions telemetry.
    /// </summary>
    Task UpdateSshSessionsSummaryAsync(long machineId, DateTimeOffset lastSeenAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the detail SshSessions payload from SshSessions telemetry.
    /// </summary>
    Task UpdateSshSessionsDetailAsync(long machineId, string payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the summary fields from HardwareHealth telemetry.
    /// </summary>
    Task UpdateHardwareHealthSummaryAsync(long machineId, bool hasDiskHealthIssue, bool hasHardwareIssue, DateTimeOffset lastSeenAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the detail HardwareHealth payload from HardwareHealth telemetry.
    /// </summary>
    Task UpdateHardwareHealthDetailAsync(long machineId, string payload, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the summary fields from PackageUpdates telemetry.
    /// </summary>
    Task UpdatePackageUpdatesSummaryAsync(long machineId, int? pendingUpdates, int? securityUpdates, DateTimeOffset lastSeenAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the summary fields from ServiceStatus telemetry.
    /// </summary>
    Task UpdateServiceStatusSummaryAsync(long machineId, int? totalServices, int? failedServices, DateTimeOffset lastSeenAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches a batch of telemetry rows with ID greater than the high-water mark
    /// and received within the streaming window.
    /// </summary>
    Task<List<MachineTelemetry>> GetTelemetryBatchAsync(long highWaterMark, DateTimeOffset streamingWindow, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns telemetry rows for the specified machine IDs and telemetry type,
    /// ordered by ReceivedAt descending.
    /// </summary>
    /// <param name="machineIds">The machine IDs to filter by.</param>
    /// <param name="telemetryType">The telemetry type identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<MachineTelemetry>> GetTelemetryByMachineIdsAndTypeAsync(List<long> machineIds, short telemetryType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a cursor-based batch of telemetry rows for the specified machines, ordered by ID ascending.
    /// Used for data export operations.
    /// </summary>
    /// <param name="machineIds">The machine IDs to filter by.</param>
    /// <param name="afterId">Return only rows with ID greater than this value.</param>
    /// <param name="batchSize">Maximum number of rows to return.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<MachineTelemetry>> GetTelemetryExportBatchAsync(List<long> machineIds, long afterId, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the latest telemetry row for each telemetry type for a machine, looking back a specified number of days.
    /// Used for machine detail views.
    /// </summary>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="daysBack">Number of days to look back from now.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<Dictionary<short, MachineTelemetry>> GetLatestTelemetryPerTypeAsync(long machineId, int daysBack, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns recent telemetry rows of a specific type for a machine, ordered by most recent first.
    /// Used for SSH session history and similar views.
    /// </summary>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="telemetryType">The telemetry type identifier.</param>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<MachineTelemetry>> GetRecentTelemetryAsync(long machineId, short telemetryType, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns health count aggregation (grouped by HealthStatus) and total security updates
    /// for all machines in a tenant, using a Machines LEFT JOIN MachineStateSummaries query.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<(List<(short HealthStatus, int Count)> HealthCounts, int TotalSecurityUpdates)> GetFleetHealthAggregationAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a filtered, sorted, paginated fleet overview joining Machines and MachineStateSummaries.
    /// Includes machine name, hostname, IP addresses, hardware model, CPU/memory usage, health status, etc.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="statusFilter">Optional health status filter (healthy/warning/critical/offline).</param>
    /// <param name="search">Optional search term for name/hostname/hardware model.</param>
    /// <param name="sortBy">Sort field name.</param>
    /// <param name="sortDescending">True for descending sort.</param>
    /// <param name="skip">Number of rows to skip.</param>
    /// <param name="take">Number of rows to return.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<(List<FleetMachineRow> Rows, int TotalCount)> GetFleetMachinePageAsync(int tenantId, string? statusFilter, string? search, string sortBy, bool sortDescending, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches fleet machines with comprehensive SQL-level filtering, sorting, and pagination.
    /// Joins Machines with MachineStateSummaries and applies criteria-based WHERE clauses at the SQL level.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="parameters">Search parameters including filters, sort, and pagination.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<(List<FleetMachineRow> Rows, int TotalCount)> SearchFleetMachinesAsync(int tenantId, FleetSearchParameters parameters, CancellationToken cancellationToken = default);
}
