// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Dashboard;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using Framlux.FleetManagement.Server.Services.Telemetry;

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
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(pingService);
        ArgumentNullException.ThrowIfNull(configService);

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
        IMachineStateRepository machineStateRepo = scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();
        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);

        // Step 1: SQL-level summary aggregation using pre-computed HealthStatus.
        (List<(short HealthStatus, int Count)> healthCounts, int totalSecurityUpdates) =
            await machineStateRepo.GetFleetHealthAggregationAsync(tenantId.Value, ct);

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

        // Step 2: SQL-level filtering, sorting, and pagination via repository.
        bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        (List<FleetMachineRow> pagedRows, int totalCount) = await machineStateRepo.GetFleetMachinePageAsync(
            tenantId.Value, statusFilter, search, sortBy, desc, (page - 1) * pageSize, pageSize, ct);

        // Step 3: Build DTOs from paged subset only.
        List<FleetMachineDto> pagedDtos = pagedRows.Select(row => new FleetMachineDto
        {
            Id = row.Id,
            Name = row.Name,
            Hostname = row.Hostname,
            IpAddress = ParseFirstIp(row.IpAddresses),
            HardwareModel = row.HardwareModel,
            HealthStatus = (MachineHealthStatus)row.HealthStatus,
            CpuUsagePercent = row.CpuUsagePercent,
            MemoryUsagePercent = row.MemoryUsagePercent,
            IsOnline = row.HealthStatus != 3,
            LastPing = row.LastSeenAt,
            PendingUpdates = row.PendingUpdates ?? 0,
            SecurityUpdates = row.SecurityUpdates ?? 0,
            FailedServices = row.FailedServices ?? 0,
            TotalServices = row.TotalServices ?? 0,
            MaxDiskUsagePercent = row.MaxDiskUsagePercent,
            HasDiskHealthIssue = row.HasDiskHealthIssue ?? false,
            HasHardwareIssue = row.HasHardwareIssue ?? false,
        }).ToList();

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
        IMachineRepository machineRepo = scope.ServiceProvider.GetRequiredService<IMachineRepository>();
        IMachineStateRepository machineStateRepo = scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();

        Machine? machine = await machineRepo.GetMachineAsync(machineId, tenantId.Value, ct);

        if (machine is null)
        {
            return null;
        }

        MachineStateSummary? state = await machineStateRepo.GetSummaryForMachineAsync(machineId, ct);

        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);
        bool isOnline = await _pingService.IsOnlineAsync(machineId, onlineThreshold);
        DateTimeOffset? lastPing = await _pingService.GetLastPingAsync(machineId);

        // Fetch latest raw telemetry per type for this machine.
        Dictionary<short, MachineTelemetry> latestByType = await machineStateRepo.GetLatestTelemetryPerTypeAsync(machineId, 7, ct);

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
        List<MachineTelemetry> sshTelemetry = await machineStateRepo.GetRecentTelemetryAsync(machineId, TelemetryTypeIds.SshSessions, 20, ct);
        List<SshSessionPayload> sshSessions = sshTelemetry
            .Select(t => JsonSerializer.Deserialize<SshSessionPayload>(t.Payload, JsonDefaults.SnakeCase))
            .Where(s => s is not null)
            .Select(s => s!)
            .ToList();

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
        catch (JsonException)
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
        catch (JsonException)
        {
            return null;
        }
    }
}

