// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Models;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Searches machines using advanced filter criteria across scalar and telemetry data.
/// Uses a fast SQL-paginated path for all common queries, including health status and last seen
/// filters which use pre-computed database columns. Falls back to a load-all path only when
/// JSONB filters (disk, hardware) cannot be pushed to SQL on the current dialect (SQLite).
/// On PostgreSQL, all filters and sorts are handled at the SQL level.
/// </summary>
public sealed class MachineSearchService : IMachineSearchService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMachinePingService _pingService;
    private readonly ServerConfigurationService _configService;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineSearchService"/> class.
    /// </summary>
    public MachineSearchService(
        IServiceScopeFactory scopeFactory,
        IMachinePingService pingService,
        ServerConfigurationService configService)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(pingService);
        ArgumentNullException.ThrowIfNull(configService);

        _scopeFactory = scopeFactory;
        _pingService = pingService;
        _configService = configService;
    }

    /// <inheritdoc/>
    public async Task<PaginatedResponse<FleetMachineDto>> SearchAsync(
        MachineSearchCriteria criteria,
        int? tenantId,
        CancellationToken ct)
    {
        int page = criteria.Page < 1 ? 1 : criteria.Page;
        int pageSize = (criteria.PageSize < 1) || (criteria.PageSize > 100) ? 25 : criteria.PageSize;

        if (tenantId is null)
        {
            return new PaginatedResponse<FleetMachineDto>
            {
                Items = [],
                Page = page,
                PageSize = pageSize,
                TotalCount = 0,
            };
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        IMachineStateRepository machineStateRepo = scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();

        return await SearchSqlPaginatedAsync(machineStateRepo, criteria, tenantId.Value, page, pageSize, ct);
    }

    /// <summary>
    /// Fast path: count, sort, and paginate at the SQL level, then resolve Redis and
    /// JSONB data only for the paged subset.
    /// </summary>
    private async Task<PaginatedResponse<FleetMachineDto>> SearchSqlPaginatedAsync(
        IMachineStateRepository machineStateRepo,
        MachineSearchCriteria criteria,
        int tenantId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        FleetSearchParameters searchParams = BuildSearchParameters(criteria, page, pageSize);
        (List<FleetMachineRow> pagedRows, int totalCount) = await machineStateRepo.SearchFleetMachinesAsync(tenantId, searchParams, ct);

        // Resolve Redis only for the paged subset.
        List<long> pagedIds = pagedRows.Select(r => r.Id).ToList();
        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);
        Dictionary<long, bool> onlineMap = await _pingService.AreOnlineAsync(pagedIds, onlineThreshold);
        Dictionary<long, DateTimeOffset?> lastPingMap = await _pingService.GetLastPingsAsync(pagedIds);

        // Build DTOs for the paged subset only.
        List<FleetMachineDto> pagedDtos = BuildDtos(pagedRows, onlineMap, lastPingMap);

        // Enrich the paged subset with health computation from Redis+scalar data.
        EnrichWithHealth(pagedDtos, pagedRows, onlineMap);

        return new PaginatedResponse<FleetMachineDto>
        {
            Items = pagedDtos,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
        };
    }

    private static FleetSearchParameters BuildSearchParameters(MachineSearchCriteria criteria, int page, int pageSize)
    {
        List<short>? healthStatusValues = null;
        if (string.IsNullOrWhiteSpace(criteria.HealthStatus) == false)
        {
            HashSet<MachineHealthStatus> statuses = ParseHealthStatuses(criteria.HealthStatus);
            if (statuses.Count > 0)
            {
                healthStatusValues = statuses.Select(hs => (short)hs).ToList();
            }
        }

        OperatingSystems? osFilter = null;
        if (string.IsNullOrWhiteSpace(criteria.Os) == false &&
            Enum.TryParse<OperatingSystems>(criteria.Os, true, out OperatingSystems osEnum))
        {
            osFilter = osEnum;
        }

        MachineTypes? typeFilter = null;
        if (string.IsNullOrWhiteSpace(criteria.Type) == false &&
            Enum.TryParse<MachineTypes>(criteria.Type, true, out MachineTypes typeEnum))
        {
            typeFilter = typeEnum;
        }

        return new FleetSearchParameters
        {
            Search = criteria.Search,
            Os = osFilter,
            MachineType = typeFilter,
            CpuMin = criteria.CpuMin,
            CpuMax = criteria.CpuMax,
            MemoryMin = criteria.MemoryMin,
            MemoryMax = criteria.MemoryMax,
            PendingUpdatesMin = criteria.PendingUpdatesMin,
            SecurityUpdatesMin = criteria.SecurityUpdatesMin,
            FailedServicesMin = criteria.FailedServicesMin,
            DiskMin = criteria.DiskMin,
            DiskMax = criteria.DiskMax,
            HasDiskHealthIssue = criteria.HasDiskHealthIssue,
            HasHardwareIssue = criteria.HasHardwareIssue,
            HealthStatusValues = healthStatusValues,
            LastSeenAfter = criteria.LastSeenAfter,
            LastSeenBefore = criteria.LastSeenBefore,
            SortBy = criteria.SortBy,
            SortDescending = string.Equals(criteria.SortDir, "desc", StringComparison.OrdinalIgnoreCase),
            Skip = (page - 1) * pageSize,
            Take = pageSize,
        };
    }

    private static List<FleetMachineDto> BuildDtos(
        List<FleetMachineRow> rows,
        Dictionary<long, bool> onlineMap,
        Dictionary<long, DateTimeOffset?> lastPingMap)
    {
        List<FleetMachineDto> dtos = new(rows.Count);

        foreach (FleetMachineRow row in rows)
        {
            bool isOnline = onlineMap.GetValueOrDefault(row.Id, false);
            DateTimeOffset? lastPing = row.LastSeenAt ?? lastPingMap.GetValueOrDefault(row.Id);
            MachineHealthStatus health = (MachineHealthStatus)row.HealthStatus;

            dtos.Add(new FleetMachineDto
            {
                Id = row.Id,
                Name = row.Name,
                Hostname = row.Hostname,
                IpAddress = ParseFirstIp(row.IpAddresses),
                HardwareModel = row.HardwareModel,
                HealthStatus = health,
                CpuUsagePercent = row.CpuUsagePercent,
                MemoryUsagePercent = row.MemoryUsagePercent,
                IsOnline = isOnline,
                LastPing = lastPing,
                PendingUpdates = row.PendingUpdates ?? 0,
                SecurityUpdates = row.SecurityUpdates ?? 0,
                FailedServices = row.FailedServices ?? 0,
                TotalServices = row.TotalServices ?? 0,
                MaxDiskUsagePercent = row.MaxDiskUsagePercent,
                HasDiskHealthIssue = row.HasDiskHealthIssue ?? false,
                HasHardwareIssue = row.HasHardwareIssue ?? false,
            });
        }

        return dtos;
    }

    private static void EnrichWithHealth(
        List<FleetMachineDto> dtos,
        List<FleetMachineRow> rows,
        Dictionary<long, bool> onlineMap)
    {
        Dictionary<long, FleetMachineRow> rowMap = rows.ToDictionary(r => r.Id);

        foreach (FleetMachineDto dto in dtos)
        {
            if (rowMap.TryGetValue(dto.Id, out FleetMachineRow? row) == false)
            {
                continue;
            }

            bool isOnline = onlineMap.GetValueOrDefault(dto.Id, false);
            dto.HealthStatus = ComputeDetailedHealth(isOnline, row, dto);
        }
    }

    private static MachineHealthStatus ComputeDetailedHealth(
        bool isOnline, FleetMachineRow row, FleetMachineDto dto)
    {
        if (isOnline == false)
        {
            return MachineHealthStatus.Offline;
        }

        // Critical checks.
        if ((row.FailedServices > 0) ||
            (row.CpuUsagePercent >= 95) ||
            (row.MemoryUsagePercent >= 95))
        {
            return MachineHealthStatus.Critical;
        }

        if ((dto.MaxDiskUsagePercent >= 95) || (dto.HasDiskHealthIssue == true) || (dto.HasHardwareIssue == true))
        {
            return MachineHealthStatus.Critical;
        }

        // Warning checks.
        if ((row.CpuUsagePercent >= 80) || (row.MemoryUsagePercent >= 80))
        {
            return MachineHealthStatus.Warning;
        }

        if (dto.MaxDiskUsagePercent >= 80)
        {
            return MachineHealthStatus.Warning;
        }

        return MachineHealthStatus.Healthy;
    }

    private static HashSet<MachineHealthStatus> ParseHealthStatuses(string healthStatusFilter)
    {
        HashSet<MachineHealthStatus> statuses = [];
        string[] parts = healthStatusFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string part in parts)
        {
            MachineHealthStatus? status = part.ToLowerInvariant() switch
            {
                "healthy" => MachineHealthStatus.Healthy,
                "warning" => MachineHealthStatus.Warning,
                "critical" => MachineHealthStatus.Critical,
                "offline" => MachineHealthStatus.Offline,
                _ => null
            };

            if (status.HasValue)
            {
                statuses.Add(status.Value);
            }
        }

        return statuses;
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
