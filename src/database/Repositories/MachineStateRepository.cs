// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IMachineStateRepository
{
    /// <inheritdoc/>
    public async Task InsertSummaryAsync(MachineStateSummary summary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summary);
        await _db.InsertAsync(summary, token: cancellationToken);
    }

    /// <inheritdoc/>
    public async Task InsertDetailAsync(MachineStateDetail detail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(detail);
        await _db.InsertAsync(detail, token: cancellationToken);
    }

    /// <inheritdoc/>
    public async Task BulkInsertTelemetryAsync(List<MachineTelemetry> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        await _db.BulkCopyAsync(new BulkCopyOptions { BulkCopyType = BulkCopyType.MultipleRows }, rows, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task InsertTelemetryAsync(MachineTelemetry row, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        await _db.InsertAsync(row, token: cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<int>> GetDistinctTenantIdsAsync(CancellationToken cancellationToken)
    {
        return await _db.MachineStateSummaries
            .Select(s => s.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> SweepHealthStatusAsync(string sql, int tenantId, int onlineThresholdSeconds, CancellationToken cancellationToken)
    {
        int rowsAffected = await _db.ExecuteAsync(
            sql,
            cancellationToken,
            new DataParameter("tenantId", tenantId),
            new DataParameter("onlineThresholdSeconds", onlineThresholdSeconds));

        return rowsAffected;
    }

    /// <inheritdoc/>
    public async Task<MachineStateSummary?> GetSummaryForMachineAsync(long machineId, CancellationToken cancellationToken)
    {
        return await _db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<MachineStateSummary>> GetSummariesForTenantMachinesAsync(int tenantId, CancellationToken cancellationToken)
    {
        return await _db.MachineStateSummaries
            .Where(s => _db.Machines.Any(m => (m.Id == s.MachineId) && (m.TenantId == tenantId) && (m.IsDeleted == false)))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Dictionary<long, string?>> GetHostnameMapAsync(List<long> machineIds, CancellationToken cancellationToken)
    {
        if (machineIds.Count == 0)
        {
            return new Dictionary<long, string?>();
        }

        return await _db.MachineStateSummaries
            .Where(s => machineIds.Contains(s.MachineId))
            .ToDictionaryAsync(s => s.MachineId, s => s.Hostname, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Dictionary<long, string>> GetNameMapAsync(List<long> machineIds, CancellationToken cancellationToken)
    {
        if (machineIds.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        return await _db.MachineStateSummaries
            .Where(s => machineIds.Contains(s.MachineId))
            .ToDictionaryAsync(s => s.MachineId, s => s.Name, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Dictionary<long, MachineStateSummary>> GetSummariesByMachineIdsAsync(List<long> machineIds, CancellationToken cancellationToken)
    {
        if (machineIds.Count == 0)
        {
            return new Dictionary<long, MachineStateSummary>();
        }

        return await _db.MachineStateSummaries
            .Where(s => machineIds.Contains(s.MachineId))
            .ToDictionaryAsync(s => s.MachineId, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<MachineStateSummary>> GetSummaryListByMachineIdsAsync(List<long> machineIds, CancellationToken cancellationToken)
    {
        if (machineIds.Count == 0)
        {
            return [];
        }

        return await _db.MachineStateSummaries
            .Where(s => machineIds.Contains(s.MachineId))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateSummaryNameAsync(long machineId, string name, CancellationToken cancellationToken)
    {
        await _db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .Set(s => s.Name, name)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateSystemInfoSummaryAsync(long machineId, string? hostname, string? hardwareModel, string? ipAddresses, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
    {
        await _db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .Set(s => s.Hostname, hostname)
            .Set(s => s.HardwareModel, hardwareModel)
            .Set(s => s.IpAddresses, ipAddresses)
            .Set(s => s.LastSeenAt, lastSeenAt)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateSystemInfoDetailAsync(long machineId, string? hardwareVendor, string? hardwareSerial, string? cpuBrand, int? cpuCores, long? memoryTotalBytes, long? uptimeSeconds, string? biosVersion, CancellationToken cancellationToken)
    {
        await _db.MachineStateDetails
            .Where(d => d.MachineId == machineId)
            .Set(d => d.HardwareVendor, hardwareVendor)
            .Set(d => d.HardwareSerial, hardwareSerial)
            .Set(d => d.CpuBrand, cpuBrand)
            .Set(d => d.CpuCores, cpuCores)
            .Set(d => d.MemoryTotalBytes, memoryTotalBytes)
            .Set(d => d.UptimeSeconds, uptimeSeconds)
            .Set(d => d.BiosVersion, biosVersion)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateOsVersionSummaryAsync(long machineId, string? osName, string? osVersion, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
    {
        await _db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .Set(s => s.OsName, osName)
            .Set(s => s.OsVersion, osVersion)
            .Set(s => s.LastSeenAt, lastSeenAt)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateOsVersionDetailAsync(long machineId, string? kernel, CancellationToken cancellationToken)
    {
        await _db.MachineStateDetails
            .Where(d => d.MachineId == machineId)
            .Set(d => d.Kernel, kernel)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateCpuInfoSummaryAsync(long machineId, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
    {
        await _db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .Set(s => s.LastSeenAt, lastSeenAt)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateCpuInfoDetailAsync(long machineId, string? cpuType, int? physicalCpus, int? logicalCpus, CancellationToken cancellationToken)
    {
        await _db.MachineStateDetails
            .Where(d => d.MachineId == machineId)
            .Set(d => d.CpuType, cpuType)
            .Set(d => d.CpuPhysicalCpus, physicalCpus)
            .Set(d => d.CpuLogicalCpus, logicalCpus)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateMemoryInfoSummaryAsync(long machineId, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
    {
        await _db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .Set(s => s.LastSeenAt, lastSeenAt)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateMemoryInfoDetailAsync(long machineId, long? swapTotalBytes, long? swapFreeBytes, CancellationToken cancellationToken)
    {
        await _db.MachineStateDetails
            .Where(d => d.MachineId == machineId)
            .Set(d => d.SwapTotalBytes, swapTotalBytes)
            .Set(d => d.SwapFreeBytes, swapFreeBytes)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateDiskInfoSummaryAsync(long machineId, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
    {
        await _db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .Set(s => s.LastSeenAt, lastSeenAt)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateDiskInfoDetailAsync(long machineId, string payload, CancellationToken cancellationToken)
    {
        await _db.MachineStateDetails
            .Where(d => d.MachineId == machineId)
            .Set(d => d.DiskInfos, payload)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateCpuUsageSummaryAsync(long machineId, int? cpuUsagePercent, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
    {
        await _db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .Set(s => s.CpuUsagePercent, cpuUsagePercent)
            .Set(s => s.LastSeenAt, lastSeenAt)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateMemoryUsageSummaryAsync(long machineId, int? memoryUsagePercent, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
    {
        await _db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .Set(s => s.MemoryUsagePercent, memoryUsagePercent)
            .Set(s => s.LastSeenAt, lastSeenAt)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateMemoryUsageDetailAsync(long machineId, long? memoryUsedBytes, CancellationToken cancellationToken)
    {
        await _db.MachineStateDetails
            .Where(d => d.MachineId == machineId)
            .Set(d => d.MemoryUsedBytes, memoryUsedBytes)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateDiskUsageSummaryAsync(long machineId, int maxDiskUsagePercent, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
    {
        await _db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .Set(s => s.MaxDiskUsagePercent, maxDiskUsagePercent)
            .Set(s => s.LastSeenAt, lastSeenAt)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateDiskUsageDetailAsync(long machineId, string payload, CancellationToken cancellationToken)
    {
        await _db.MachineStateDetails
            .Where(d => d.MachineId == machineId)
            .Set(d => d.DiskUsages, payload)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateSshSessionsSummaryAsync(long machineId, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
    {
        await _db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .Set(s => s.LastSeenAt, lastSeenAt)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateSshSessionsDetailAsync(long machineId, string payload, CancellationToken cancellationToken)
    {
        await _db.MachineStateDetails
            .Where(d => d.MachineId == machineId)
            .Set(d => d.SshSessions, payload)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateHardwareHealthSummaryAsync(long machineId, bool hasDiskHealthIssue, bool hasHardwareIssue, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
    {
        await _db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .Set(s => s.HasDiskHealthIssue, hasDiskHealthIssue)
            .Set(s => s.HasHardwareIssue, hasHardwareIssue)
            .Set(s => s.LastSeenAt, lastSeenAt)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateHardwareHealthDetailAsync(long machineId, string payload, CancellationToken cancellationToken)
    {
        await _db.MachineStateDetails
            .Where(d => d.MachineId == machineId)
            .Set(d => d.HardwareHealth, payload)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdatePackageUpdatesSummaryAsync(long machineId, int? pendingUpdates, int? securityUpdates, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
    {
        await _db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .Set(s => s.PendingUpdates, pendingUpdates)
            .Set(s => s.SecurityUpdates, securityUpdates)
            .Set(s => s.LastSeenAt, lastSeenAt)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task UpdateServiceStatusSummaryAsync(long machineId, int? totalServices, int? failedServices, DateTimeOffset lastSeenAt, CancellationToken cancellationToken)
    {
        await _db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .Set(s => s.TotalServices, totalServices)
            .Set(s => s.FailedServices, failedServices)
            .Set(s => s.LastSeenAt, lastSeenAt)
            .UpdateAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<MachineTelemetry>> GetTelemetryBatchAsync(long highWaterMark, DateTimeOffset streamingWindow, int batchSize, CancellationToken cancellationToken)
    {
        return await _db.MachineTelemetry
            .Where(t => (t.Id > highWaterMark) && (t.ReceivedAt > streamingWindow))
            .OrderBy(t => t.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<MachineTelemetry>> GetTelemetryByMachineIdsAndTypeAsync(List<long> machineIds, short telemetryType, CancellationToken cancellationToken)
    {
        return await _db.MachineTelemetry
            .Where(t => machineIds.Contains(t.MachineId) &&
                        t.TelemetryType == telemetryType)
            .OrderByDescending(t => t.ReceivedAt)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<List<MachineTelemetry>> GetTelemetryExportBatchAsync(List<long> machineIds, long afterId, int batchSize, CancellationToken cancellationToken)
    {
        return await _db.MachineTelemetry
            .Where(t => machineIds.Contains(t.MachineId) && t.Id > afterId)
            .OrderBy(t => t.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Dictionary<short, MachineTelemetry>> GetLatestTelemetryPerTypeAsync(long machineId, int daysBack, CancellationToken cancellationToken)
    {
        DateTimeOffset recencyCutoff = DateTimeOffset.UtcNow.AddDays(-daysBack);
        List<MachineTelemetry> latest = await _db.MachineTelemetry
            .Where(t => (t.MachineId == machineId) && (t.ReceivedAt > recencyCutoff))
            .GroupBy(t => t.TelemetryType)
            .Select(g => g.OrderByDescending(t => t.ReceivedAt).First())
            .ToListAsync(cancellationToken);

        return latest.ToDictionary(t => t.TelemetryType);
    }

    /// <inheritdoc/>
    public async Task<List<MachineTelemetry>> GetRecentTelemetryAsync(long machineId, short telemetryType, int limit, CancellationToken cancellationToken)
    {
        return await _db.MachineTelemetry
            .Where(t => (t.MachineId == machineId) && (t.TelemetryType == telemetryType))
            .OrderByDescending(t => t.ReceivedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<(List<(short HealthStatus, int Count)> HealthCounts, int TotalSecurityUpdates)> GetFleetHealthAggregationAsync(int tenantId, CancellationToken cancellationToken)
    {
        IQueryable<FleetMachineRow> baseQuery = BuildFleetBaseQuery(tenantId);

        List<(short HealthStatus, int Count)> healthCounts = await (
            from r in baseQuery
            group r by r.HealthStatus into g
            select new { HealthStatus = g.Key, Count = g.Count() }
        ).ToListAsync(cancellationToken)
         .ContinueWith(t => t.Result.Select(x => (x.HealthStatus, x.Count)).ToList(), cancellationToken);

        int totalSecurityUpdates = await baseQuery.SumAsync(r => r.SecurityUpdates ?? 0, cancellationToken);

        return (healthCounts, totalSecurityUpdates);
    }

    /// <inheritdoc/>
    public async Task<(List<FleetMachineRow> Rows, int TotalCount)> GetFleetMachinePageAsync(
        int tenantId, string? statusFilter, string? search, string sortBy, bool sortDescending, int skip, int take, CancellationToken cancellationToken)
    {
        IQueryable<FleetMachineRow> query = BuildFleetBaseQuery(tenantId);

        if (string.IsNullOrWhiteSpace(statusFilter) == false)
        {
            short? targetStatus = statusFilter.ToLowerInvariant() switch
            {
                "healthy" => 0,
                "warning" => 1,
                "critical" => 2,
                "offline" => 3,
                _ => null,
            };

            if (targetStatus.HasValue)
            {
                query = query.Where(r => r.HealthStatus == targetStatus.Value);
            }
        }

        if (string.IsNullOrWhiteSpace(search) == false)
        {
            string q = search.ToLowerInvariant();
            query = query.Where(r =>
                r.Name.ToLower().Contains(q) ||
                ((r.Hostname != null) && r.Hostname.ToLower().Contains(q)) ||
                ((r.HardwareModel != null) && r.HardwareModel.ToLower().Contains(q)));
        }

        int totalCount = await query.CountAsync(cancellationToken);

        IQueryable<FleetMachineRow> sortedQuery = sortBy.ToLowerInvariant() switch
        {
            "status" => sortDescending
                ? query.OrderByDescending(r => r.HealthStatus)
                : query.OrderBy(r => r.HealthStatus),
            "cpu" => sortDescending
                ? query.OrderByDescending(r => r.CpuUsagePercent ?? 0)
                : query.OrderBy(r => r.CpuUsagePercent ?? 0),
            "memory" => sortDescending
                ? query.OrderByDescending(r => r.MemoryUsagePercent ?? 0)
                : query.OrderBy(r => r.MemoryUsagePercent ?? 0),
            _ => sortDescending
                ? query.OrderByDescending(r => r.Name)
                : query.OrderBy(r => r.Name),
        };

        List<FleetMachineRow> rows = await sortedQuery
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        return (rows, totalCount);
    }

    /// <inheritdoc/>
    public async Task<(List<FleetMachineRow> Rows, int TotalCount)> SearchFleetMachinesAsync(int tenantId, FleetSearchParameters parameters, CancellationToken cancellationToken)
    {
        IQueryable<FleetMachineRow> query = BuildFleetBaseQuery(tenantId);

        // Text search
        if (string.IsNullOrWhiteSpace(parameters.Search) == false)
        {
            string searchLower = parameters.Search.ToLowerInvariant();
            query = query.Where(r =>
                r.Name.ToLower().Contains(searchLower) ||
                ((r.Hostname != null) && r.Hostname.ToLower().Contains(searchLower)) ||
                ((r.HardwareModel != null) && r.HardwareModel.ToLower().Contains(searchLower)));
        }

        // OS and machine type filters
        if (parameters.Os.HasValue)
        {
            Enums.OperatingSystems os = parameters.Os.Value;
            query = query.Where(r => r.OperatingSystem == os);
        }

        if (parameters.MachineType.HasValue)
        {
            Enums.MachineTypes machineType = parameters.MachineType.Value;
            query = query.Where(r => r.MachineType == machineType);
        }

        // CPU range
        if (parameters.CpuMin.HasValue)
        {
            int cpuMin = parameters.CpuMin.Value;
            query = query.Where(r => (r.CpuUsagePercent != null) && (r.CpuUsagePercent >= cpuMin));
        }

        if (parameters.CpuMax.HasValue)
        {
            int cpuMax = parameters.CpuMax.Value;
            query = query.Where(r => (r.CpuUsagePercent != null) && (r.CpuUsagePercent <= cpuMax));
        }

        // Memory range
        if (parameters.MemoryMin.HasValue)
        {
            int memMin = parameters.MemoryMin.Value;
            query = query.Where(r => (r.MemoryUsagePercent != null) && (r.MemoryUsagePercent >= memMin));
        }

        if (parameters.MemoryMax.HasValue)
        {
            int memMax = parameters.MemoryMax.Value;
            query = query.Where(r => (r.MemoryUsagePercent != null) && (r.MemoryUsagePercent <= memMax));
        }

        // Threshold filters
        if (parameters.PendingUpdatesMin.HasValue)
        {
            int pendingMin = parameters.PendingUpdatesMin.Value;
            query = query.Where(r => (r.PendingUpdates != null) && (r.PendingUpdates >= pendingMin));
        }

        if (parameters.SecurityUpdatesMin.HasValue)
        {
            int securityMin = parameters.SecurityUpdatesMin.Value;
            query = query.Where(r => (r.SecurityUpdates != null) && (r.SecurityUpdates >= securityMin));
        }

        if (parameters.FailedServicesMin.HasValue)
        {
            int failedMin = parameters.FailedServicesMin.Value;
            query = query.Where(r => (r.FailedServices != null) && (r.FailedServices >= failedMin));
        }

        // Disk and hardware filters
        if (parameters.DiskMin.HasValue)
        {
            int diskMin = parameters.DiskMin.Value;
            query = query.Where(r => (r.MaxDiskUsagePercent != null) && (r.MaxDiskUsagePercent >= diskMin));
        }

        if (parameters.DiskMax.HasValue)
        {
            int diskMax = parameters.DiskMax.Value;
            query = query.Where(r => (r.MaxDiskUsagePercent != null) && (r.MaxDiskUsagePercent <= diskMax));
        }

        if (parameters.HasDiskHealthIssue.HasValue)
        {
            bool hasDiskIssue = parameters.HasDiskHealthIssue.Value;
            query = query.Where(r => (r.HasDiskHealthIssue != null) && (r.HasDiskHealthIssue == hasDiskIssue));
        }

        if (parameters.HasHardwareIssue.HasValue)
        {
            bool hasHwIssue = parameters.HasHardwareIssue.Value;
            query = query.Where(r => (r.HasHardwareIssue != null) && (r.HasHardwareIssue == hasHwIssue));
        }

        // Health status filter
        if (parameters.HealthStatusValues is { Count: > 0 })
        {
            List<short> statusValues = parameters.HealthStatusValues;
            query = query.Where(r => statusValues.Contains(r.HealthStatus));
        }

        // Last seen time range
        if (parameters.LastSeenAfter.HasValue)
        {
            DateTimeOffset after = parameters.LastSeenAfter.Value;
            query = query.Where(r => (r.LastSeenAt != null) && (r.LastSeenAt >= after));
        }

        if (parameters.LastSeenBefore.HasValue)
        {
            DateTimeOffset before = parameters.LastSeenBefore.Value;
            query = query.Where(r => (r.LastSeenAt != null) && (r.LastSeenAt <= before));
        }

        int totalCount = await query.CountAsync(cancellationToken);

        // Sort
        bool desc = parameters.SortDescending;
        IQueryable<FleetMachineRow> sortedQuery = parameters.SortBy?.ToLowerInvariant() switch
        {
            "status" => desc
                ? query.OrderByDescending(r => r.HealthStatus)
                : query.OrderBy(r => r.HealthStatus),
            "cpu" => desc
                ? query.OrderByDescending(r => r.CpuUsagePercent ?? 0)
                : query.OrderBy(r => r.CpuUsagePercent ?? 0),
            "memory" => desc
                ? query.OrderByDescending(r => r.MemoryUsagePercent ?? 0)
                : query.OrderBy(r => r.MemoryUsagePercent ?? 0),
            "disk" => desc
                ? query.OrderByDescending(r => r.MaxDiskUsagePercent ?? 0)
                : query.OrderBy(r => r.MaxDiskUsagePercent ?? 0),
            "updates" => desc
                ? query.OrderByDescending(r => r.PendingUpdates ?? 0)
                : query.OrderBy(r => r.PendingUpdates ?? 0),
            "lastseen" => desc
                ? query.OrderByDescending(r => r.LastSeenAt)
                : query.OrderBy(r => r.LastSeenAt),
            _ => desc
                ? query.OrderByDescending(r => r.Name)
                : query.OrderBy(r => r.Name),
        };

        List<FleetMachineRow> rows = await sortedQuery
            .Skip(parameters.Skip)
            .Take(parameters.Take)
            .ToListAsync(cancellationToken);

        return (rows, totalCount);
    }

    private IQueryable<FleetMachineRow> BuildFleetBaseQuery(int tenantId)
    {
        return from m in _db.Machines
               join s in _db.MachineStateSummaries on m.Id equals s.MachineId into stateJoin
               from s in stateJoin.DefaultIfEmpty()
               where (m.TenantId == tenantId) && (m.IsDeleted == false)
               select new FleetMachineRow
               {
                   Id = m.Id,
                   Name = m.Name,
                   OperatingSystem = m.OperatingSystem,
                   MachineType = m.MachineType,
                   Hostname = s != null ? s.Hostname : null,
                   IpAddresses = s != null ? s.IpAddresses : null,
                   HardwareModel = s != null ? s.HardwareModel : null,
                   CpuUsagePercent = s != null ? s.CpuUsagePercent : (int?)null,
                   MemoryUsagePercent = s != null ? s.MemoryUsagePercent : (int?)null,
                   PendingUpdates = s != null ? s.PendingUpdates : (int?)null,
                   SecurityUpdates = s != null ? s.SecurityUpdates : (int?)null,
                   FailedServices = s != null ? s.FailedServices : (int?)null,
                   TotalServices = s != null ? s.TotalServices : (int?)null,
                   HealthStatus = s != null ? s.HealthStatus : (short)3,
                   LastSeenAt = s != null ? s.LastSeenAt : (DateTimeOffset?)null,
                   MaxDiskUsagePercent = s != null ? s.MaxDiskUsagePercent : (int?)null,
                   HasDiskHealthIssue = s != null ? s.HasDiskHealthIssue : (bool?)null,
                   HasHardwareIssue = s != null ? s.HasHardwareIssue : (bool?)null,
                   OsName = s != null ? s.OsName : null,
                   OsVersion = s != null ? s.OsVersion : null,
               };
    }
}
