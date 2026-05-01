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
}
