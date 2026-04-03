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
/// Reads MachineState cache and maps to fleet/detail DTOs.
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

        // Step 1: Load scalar-only projections (no JSONB columns) for all machines.
        List<MachineScalarRow> scalarRows = await (
            from m in db.Machines
            join s in db.MachineStates on m.Id equals s.MachineId into stateJoin
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
            }
        ).ToListAsync(ct);

        List<long> machineIds = scalarRows.Select(r => r.Id).ToList();
        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);
        Dictionary<long, bool> onlineMap = await _pingService.AreOnlineAsync(machineIds, onlineThreshold);
        Dictionary<long, DateTimeOffset?> lastPingMap = await _pingService.GetLastPingsAsync(machineIds);

        // Step 2: Build lightweight DTOs using scalar columns only.
        // Health is approximated from scalar metrics (CPU, memory, failed services).
        List<FleetMachineDto> allDtos = [];
        int onlineCount = 0;
        int warningCount = 0;
        int criticalCount = 0;
        int totalSecurityUpdates = 0;

        foreach (MachineScalarRow row in scalarRows)
        {
            bool isOnline = onlineMap.GetValueOrDefault(row.Id, false);
            DateTimeOffset? lastPing = lastPingMap.GetValueOrDefault(row.Id);

            MachineHealthStatus health = ComputeScalarHealth(
                isOnline, row.StateCpuUsagePercent, row.StateMemoryUsagePercent, row.StateFailedServices);

            if (isOnline)
            {
                onlineCount++;
            }

            if (health == MachineHealthStatus.Warning)
            {
                warningCount++;
            }

            if (health == MachineHealthStatus.Critical)
            {
                criticalCount++;
            }

            totalSecurityUpdates += row.StateSecurityUpdates ?? 0;

            allDtos.Add(new FleetMachineDto
            {
                Id = row.Id,
                Name = row.Name,
                Hostname = row.StateHostname,
                IpAddress = ParseFirstIp(row.StateIpAddresses),
                HardwareModel = row.StateHardwareModel,
                HealthStatus = health,
                CpuUsagePercent = row.StateCpuUsagePercent,
                MemoryUsagePercent = row.StateMemoryUsagePercent,
                IsOnline = isOnline,
                LastPing = lastPing,
                PendingUpdates = row.StatePendingUpdates ?? 0,
                SecurityUpdates = row.StateSecurityUpdates ?? 0,
                FailedServices = row.StateFailedServices ?? 0,
                TotalServices = row.StateTotalServices ?? 0,
            });
        }

        FleetSummaryDto summary = new()
        {
            TotalMachines = scalarRows.Count,
            OnlineMachines = onlineCount,
            OfflineCount = scalarRows.Count - onlineCount,
            WarningCount = warningCount,
            CriticalCount = criticalCount,
            SecurityUpdates = totalSecurityUpdates,
        };

        // Step 3: Apply filters.
        IEnumerable<FleetMachineDto> filtered = allDtos;

        if (string.IsNullOrWhiteSpace(statusFilter) == false)
        {
            MachineHealthStatus? targetStatus = statusFilter.ToLowerInvariant() switch
            {
                "healthy" => MachineHealthStatus.Healthy,
                "warning" => MachineHealthStatus.Warning,
                "critical" => MachineHealthStatus.Critical,
                "offline" => MachineHealthStatus.Offline,
                _ => null
            };

            if (targetStatus.HasValue)
            {
                filtered = filtered.Where(m => m.HealthStatus == targetStatus.Value);
            }
        }

        if (string.IsNullOrWhiteSpace(search) == false)
        {
            string q = search.ToLowerInvariant();
            filtered = filtered.Where(m =>
                m.Name.ToLowerInvariant().Contains(q) ||
                (m.Hostname?.ToLowerInvariant().Contains(q) ?? false) ||
                (m.IpAddress?.ToLowerInvariant().Contains(q) ?? false) ||
                (m.HardwareModel?.ToLowerInvariant().Contains(q) ?? false));
        }

        // Step 4: Sort.
        bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        List<FleetMachineDto> sortedList = sortBy?.ToLowerInvariant() switch
        {
            "status" => desc
                ? filtered.OrderByDescending(m => m.HealthStatus).ToList()
                : filtered.OrderBy(m => m.HealthStatus).ToList(),
            "cpu" => desc
                ? filtered.OrderByDescending(m => m.CpuUsagePercent ?? 0).ToList()
                : filtered.OrderBy(m => m.CpuUsagePercent ?? 0).ToList(),
            "memory" => desc
                ? filtered.OrderByDescending(m => m.MemoryUsagePercent ?? 0).ToList()
                : filtered.OrderBy(m => m.MemoryUsagePercent ?? 0).ToList(),
            "disk" => desc
                ? filtered.OrderByDescending(m => m.MaxDiskUsagePercent ?? 0).ToList()
                : filtered.OrderBy(m => m.MaxDiskUsagePercent ?? 0).ToList(),
            _ => desc
                ? filtered.OrderByDescending(m => m.Name).ToList()
                : filtered.OrderBy(m => m.Name).ToList(),
        };

        int totalCount = sortedList.Count;

        // Step 5: Paginate.
        List<FleetMachineDto> pagedDtos = sortedList
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        // Step 6: Enrich the paged subset with JSONB data (disk/hardware) for accurate display.
        List<long> pagedIds = pagedDtos.Select(m => m.Id).ToList();
        if (pagedIds.Count > 0)
        {
            Dictionary<long, MachineState> pagedStates = await db.MachineStates
                .Where(s => pagedIds.Contains(s.MachineId))
                .ToDictionaryAsync(s => s.MachineId, ct);

            foreach (FleetMachineDto dto in pagedDtos)
            {
                if (pagedStates.TryGetValue(dto.Id, out MachineState? state) == false)
                {
                    continue;
                }

                if (state.DiskUsages is not null)
                {
                    List<DiskUsageEntryDto>? disks = JsonSerializer.Deserialize<List<DiskUsageEntryDto>>(state.DiskUsages, JsonDefaults.SnakeCase);
                    if (disks is { Count: > 0 })
                    {
                        dto.MaxDiskUsagePercent = disks.Max(d => d.UsagePercent);
                    }
                }

                if (state.HardwareHealth is not null)
                {
                    HardwareHealthPayload? hw = JsonSerializer.Deserialize<HardwareHealthPayload>(state.HardwareHealth, JsonDefaults.SnakeCase);
                    if (hw is not null)
                    {
                        dto.HasDiskHealthIssue = hw.DiskSmart.Exists(d =>
                            string.Equals(d.HealthStatus, "FAILED", StringComparison.OrdinalIgnoreCase));
                        dto.HasHardwareIssue = hw.Fans.Exists(f => f.Rpm == 0) ||
                            hw.PowerSupplies.Exists(p =>
                                string.Equals(p.Status, "ok", StringComparison.OrdinalIgnoreCase) == false);
                    }
                }

                // Recompute health with full data for paged machines.
                dto.HealthStatus = HealthComputer.Compute(state, dto.IsOnline);
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

    /// <summary>
    /// Computes health from scalar metrics only (no JSONB deserialization).
    /// Used for summary aggregation across all machines.
    /// </summary>
    private static MachineHealthStatus ComputeScalarHealth(bool isOnline, int? cpuPercent, int? memPercent, int? failedServices)
    {
        if (isOnline == false)
        {
            return MachineHealthStatus.Offline;
        }

        if ((cpuPercent >= 95) || (memPercent >= 95) || (failedServices > 0))
        {
            return MachineHealthStatus.Critical;
        }

        if ((cpuPercent >= 80) || (memPercent >= 80))
        {
            return MachineHealthStatus.Warning;
        }

        return MachineHealthStatus.Healthy;
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

        MachineState? state = await db.MachineStates
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
            TelemetryLastUpdated = state?.LastTelemetryAt,
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
}
