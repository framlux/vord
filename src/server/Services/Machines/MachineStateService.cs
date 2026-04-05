// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Dashboard;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using Framlux.FleetManagement.Server.Services.Telemetry;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Reads MachineStateSummary cache and maps to fleet/detail DTOs.
/// </summary>
public sealed class MachineStateService : IMachineStateService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMachinePingService _pingService;
    private readonly ServerConfigurationService _configService;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineStateService"/> class.
    /// </summary>
    public MachineStateService(IServiceScopeFactory scopeFactory, IMachinePingService pingService, ServerConfigurationService configService)
    {
        _scopeFactory = scopeFactory;
        _pingService = pingService;
        _configService = configService;
    }

    /// <inheritdoc/>
    public async Task<PaginatedFleetOverviewDto> GetFleetOverviewAsync(
        int page,
        int pageSize,
        int? tenantId,
        string? search,
        string? statusFilter,
        string sortBy,
        string sortDir,
        CancellationToken ct)
    {
        if (page < 1)
        {
            page = 1;
        }

        if ((pageSize < 1) || (pageSize > 100))
        {
            pageSize = 25;
        }

        if (tenantId is null)
        {
            return new PaginatedFleetOverviewDto
            {
                Summary = new FleetSummaryDto(),
                Machines = [],
                Page = page,
                PageSize = pageSize,
                TotalCount = 0,
            };
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();
        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);

        // Step 1: SQL-level summary aggregation using pre-computed HealthStatus.
        IQueryable<MachineScalarRow> baseQuery =
            from m in db.Machines
            join s in db.MachineStateSummaries on m.Id equals s.MachineId into stateJoin
            from s in stateJoin.DefaultIfEmpty()
            where m.TenantId == tenantId.Value && m.IsDeleted == false
            select new MachineScalarRow
            {
                Id = m.Id,
                Name = m.Name,
                StateHostname = s != null ? s.Hostname : null,
                StateIpAddresses = s != null ? s.IpAddresses : null,
                StateHardwareModel = s != null ? s.HardwareModel : null,
                StateCpuUsagePercent = s != null ? s.CpuUsagePercent : (int?)null,
                StateMemoryUsagePercent = s != null ? s.MemoryUsagePercent : (int?)null,
                StatePendingUpdates = s != null ? s.PendingUpdates : (int?)null,
                StateSecurityUpdates = s != null ? s.SecurityUpdates : (int?)null,
                StateFailedServices = s != null ? s.FailedServices : (int?)null,
                StateTotalServices = s != null ? s.TotalServices : (int?)null,
                StateHealthStatus = s != null ? s.HealthStatus : (short)3,
                StateLastPingAt = s != null ? s.LastSeenAt : (DateTimeOffset?)null,
            };

        // Summary stats via SQL GROUP BY — no need to load all rows.
        List<HealthCountRow> healthCounts = await (
            from r in baseQuery
            group r by r.StateHealthStatus into g
            select new HealthCountRow { HealthStatus = g.Key, Count = g.Count() }
        ).ToListAsync(ct);

        int totalSecurityUpdates = await baseQuery.SumAsync(r => r.StateSecurityUpdates ?? 0, ct);

        int totalMachines = healthCounts.Sum(h => h.Count);
        int healthyCount = healthCounts.Where(h => h.HealthStatus == 0).Sum(h => h.Count);
        int warningCount = healthCounts.Where(h => h.HealthStatus == 1).Sum(h => h.Count);
        int criticalCount = healthCounts.Where(h => h.HealthStatus == 2).Sum(h => h.Count);
        int offlineCount = healthCounts.Where(h => h.HealthStatus == 3).Sum(h => h.Count);

        FleetSummaryDto summary = new()
        {
            TotalMachines = totalMachines,
            OnlineMachines = totalMachines - offlineCount,
            OfflineCount = offlineCount,
            WarningCount = warningCount,
            CriticalCount = criticalCount,
            SecurityUpdates = totalSecurityUpdates,
        };

        // Step 2: SQL-level filtering.
        IQueryable<MachineScalarRow> filteredQuery = baseQuery;

        if (string.IsNullOrWhiteSpace(statusFilter) == false)
        {
            short? targetStatus = statusFilter.ToLowerInvariant() switch
            {
                "healthy" => 0,
                "warning" => 1,
                "critical" => 2,
                "offline" => 3,
                _ => null
            };

            if (targetStatus.HasValue)
            {
                filteredQuery = filteredQuery.Where(r => r.StateHealthStatus == targetStatus.Value);
            }
        }

        if (string.IsNullOrWhiteSpace(search) == false)
        {
            string q = search.ToLowerInvariant();
            filteredQuery = filteredQuery.Where(r =>
                r.Name.ToLower().Contains(q) ||
                (r.StateHostname != null && r.StateHostname.ToLower().Contains(q)) ||
                (r.StateHardwareModel != null && r.StateHardwareModel.ToLower().Contains(q)));
        }

        // Step 3: SQL-level count, sort, and paginate.
        int totalCount = await filteredQuery.CountAsync(ct);

        bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        IQueryable<MachineScalarRow> sortedQuery = sortBy.ToLowerInvariant() switch
        {
            "status" => desc
                ? filteredQuery.OrderByDescending(r => r.StateHealthStatus)
                : filteredQuery.OrderBy(r => r.StateHealthStatus),
            "cpu" => desc
                ? filteredQuery.OrderByDescending(r => r.StateCpuUsagePercent ?? 0)
                : filteredQuery.OrderBy(r => r.StateCpuUsagePercent ?? 0),
            "memory" => desc
                ? filteredQuery.OrderByDescending(r => r.StateMemoryUsagePercent ?? 0)
                : filteredQuery.OrderBy(r => r.StateMemoryUsagePercent ?? 0),
            _ => desc
                ? filteredQuery.OrderByDescending(r => r.Name)
                : filteredQuery.OrderBy(r => r.Name),
        };

        List<MachineScalarRow> pagedRows = await sortedQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        // Step 4: Build DTOs from paged subset only.
        List<FleetMachineDto> pagedDtos = pagedRows.Select(row => new FleetMachineDto
        {
            Id = row.Id,
            Name = row.Name,
            Hostname = row.StateHostname,
            IpAddress = ParseFirstIp(row.StateIpAddresses),
            HardwareModel = row.StateHardwareModel,
            HealthStatus = (MachineHealthStatus)row.StateHealthStatus,
            CpuUsagePercent = row.StateCpuUsagePercent,
            MemoryUsagePercent = row.StateMemoryUsagePercent,
            IsOnline = row.StateHealthStatus != 3,
            LastPing = row.StateLastPingAt,
            PendingUpdates = row.StatePendingUpdates ?? 0,
            SecurityUpdates = row.StateSecurityUpdates ?? 0,
            FailedServices = row.StateFailedServices ?? 0,
            TotalServices = row.StateTotalServices ?? 0,
        }).ToList();

        // Step 5: Enrich the paged subset with JSONB data (disk/hardware) for accurate display.
        List<long> pagedIds = pagedDtos.Select(m => m.Id).ToList();
        if (pagedIds.Count > 0)
        {
            Dictionary<long, MachineStateSummary> pagedStates = await db.MachineStateSummaries
                .Where(s => pagedIds.Contains(s.MachineId))
                .ToDictionaryAsync(s => s.MachineId, ct);

            foreach (FleetMachineDto dto in pagedDtos)
            {
                if (pagedStates.TryGetValue(dto.Id, out MachineStateSummary? state) == false)
                {
                    continue;
                }

                dto.MaxDiskUsagePercent = state.MaxDiskUsagePercent;
                dto.HasDiskHealthIssue = state.HasDiskHealthIssue ?? false;
                dto.HasHardwareIssue = state.HasHardwareIssue ?? false;
            }
        }

        return new PaginatedFleetOverviewDto
        {
            Summary = summary,
            Machines = pagedDtos,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
        };
    }

    /// <inheritdoc/>
    public async Task<MachineDetailDto?> GetMachineDetailAsync(long machineId, int? tenantId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return null;
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        Machine? machine = await db.Machines
            .Where(m => m.Id == machineId && m.TenantId == tenantId.Value && m.IsDeleted == false)
            .FirstOrDefaultAsync(ct);

        if (machine is null)
        {
            return null;
        }

        MachineStateSummary? state = await db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .FirstOrDefaultAsync(ct);

        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);
        bool isOnline = await _pingService.IsOnlineAsync(machineId, onlineThreshold);
        DateTimeOffset? lastPing = await _pingService.GetLastPingAsync(machineId);

        // Fetch latest raw telemetry per type for this machine.
        Dictionary<short, MachineTelemetry> latestByType = await GetLatestTelemetryByType(db, machineId, ct);

        SystemInfoPayload? systemInfo = DeserializePayload<SystemInfoPayload>(latestByType, TelemetryTypeIds.SystemInfo);
        OsVersionPayload? osVersion = DeserializePayload<OsVersionPayload>(latestByType, TelemetryTypeIds.OsVersion);
        CpuUsagePayload? cpuUsage = DeserializePayload<CpuUsagePayload>(latestByType, TelemetryTypeIds.CpuUsage);
        MemoryUsagePayload? memoryUsage = DeserializePayload<MemoryUsagePayload>(latestByType, TelemetryTypeIds.MemoryUsage);
        DiskUsagePayload? diskUsages = DeserializePayload<DiskUsagePayload>(latestByType, TelemetryTypeIds.DiskUsage);
        HardwareHealthPayload? hwHealth = DeserializePayload<HardwareHealthPayload>(latestByType, TelemetryTypeIds.HardwareHealth);
        PackageUpdatesPayload? packages = DeserializePayload<PackageUpdatesPayload>(latestByType, TelemetryTypeIds.PackageUpdates);
        ServiceStatusPayload? services = DeserializePayload<ServiceStatusPayload>(latestByType, TelemetryTypeIds.ServiceStatus);

        List<ServiceEntryDto> failedServices = services?.Services
            .Where(s => string.Equals(s.ActiveState, "failed", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        // Recent SSH sessions (last 20).
        List<SshSessionPayload> sshSessions = await db.MachineTelemetry
            .Where(t => t.MachineId == machineId && t.TelemetryType == TelemetryTypeIds.SshSessions && t.DeletedAt == null)
            .OrderByDescending(t => t.ReceivedAt)
            .Take(20)
            .ToListAsync(ct)
            .ContinueWith(task => task.Result
                .Select(t => JsonSerializer.Deserialize<SshSessionPayload>(t.Payload, JsonDefaults.SnakeCase))
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList(), ct);

        MachineHealthStatus health = state is not null
            ? HealthComputer.Compute(state, isOnline)
            : (isOnline ? MachineHealthStatus.Healthy : MachineHealthStatus.Offline);

        return new MachineDetailDto
        {
            Id = machine.Id,
            Name = machine.Name,
            Hostname = state?.Hostname ?? systemInfo?.Hostname,
            IsOnline = isOnline,
            LastPing = lastPing,
            HealthStatus = health,
            SystemInfo = systemInfo,
            OsVersion = osVersion,
            CpuUsage = cpuUsage,
            MemoryUsage = memoryUsage,
            DiskUsages = diskUsages,
            HardwareHealth = hwHealth,
            PackageUpdates = packages,
            FailedServices = failedServices,
            TotalServices = services?.Services.Count ?? 0,
            RecentSshSessions = sshSessions,
            TelemetryLastUpdated = state?.LastSeenAt,
        };
    }

    private static async Task<Dictionary<short, MachineTelemetry>> GetLatestTelemetryByType(
        DatabaseContext db, long machineId, CancellationToken ct)
    {
        // Uses the composite index IX_MachineTelemetry_Active (partial index excluding soft-deleted rows).
        List<MachineTelemetry> latest = await db.MachineTelemetry
            .Where(t => t.MachineId == machineId && t.DeletedAt == null)
            .GroupBy(t => t.TelemetryType)
            .Select(g => g.OrderByDescending(t => t.ReceivedAt).First())
            .ToListAsync(ct);

        return latest.ToDictionary(t => t.TelemetryType);
    }

    private static T? DeserializePayload<T>(Dictionary<short, MachineTelemetry> map, short type) where T : class
    {
        if (map.TryGetValue(type, out MachineTelemetry? row) == false)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(row.Payload, JsonDefaults.SnakeCase);
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseFirstIp(string? ipJson)
    {
        if (string.IsNullOrEmpty(ipJson))
        {
            return null;
        }

        try
        {
            List<string>? ips = JsonSerializer.Deserialize<List<string>>(ipJson);

            return ips is { Count: > 0 } ? ips[0] : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Lightweight projection for fleet overview queries (no JSONB columns).
/// </summary>
file sealed class MachineScalarRow
{
    public long Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? StateHostname { get; init; }
    public string? StateIpAddresses { get; init; }
    public string? StateHardwareModel { get; init; }
    public int? StateCpuUsagePercent { get; init; }
    public int? StateMemoryUsagePercent { get; init; }
    public int? StatePendingUpdates { get; init; }
    public int? StateSecurityUpdates { get; init; }
    public int? StateFailedServices { get; init; }
    public int? StateTotalServices { get; init; }
    public short StateHealthStatus { get; init; }
    public DateTimeOffset? StateLastPingAt { get; init; }
}

/// <summary>
/// Projection for SQL GROUP BY health status aggregation.
/// </summary>
file sealed class HealthCountRow
{
    public short HealthStatus { get; init; }
    public int Count { get; init; }
}
