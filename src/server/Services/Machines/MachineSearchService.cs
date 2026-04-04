// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using Framlux.FleetManagement.Server.Services.Telemetry;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Server.Services.Machines;

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
    private readonly ISqlDialect _dialect;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineSearchService"/> class.
    /// </summary>
    public MachineSearchService(
        IServiceScopeFactory scopeFactory,
        IMachinePingService pingService,
        ServerConfigurationService configService,
        ISqlDialect dialect)
    {
        _scopeFactory = scopeFactory;
        _pingService = pingService;
        _configService = configService;
        _dialect = dialect;
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
        DatabaseContext db = scope.ServiceProvider.GetRequiredService<DatabaseContext>();

        if (RequiresFullScan(criteria))
        {
            return await SearchFullScanAsync(db, criteria, tenantId.Value, page, pageSize, ct);
        }

        return await SearchSqlPaginatedAsync(db, criteria, tenantId.Value, page, pageSize, ct);
    }

    /// <summary>
    /// Returns true when the criteria include filters or sort options that cannot be
    /// pushed to SQL on the current database dialect. Health status and last seen filters
    /// now use pre-computed database columns and no longer require a full scan.
    /// </summary>
    private bool RequiresFullScan(MachineSearchCriteria criteria)
    {
        // Disk and hardware issue filters require JSONB. On PostgreSQL these are
        // pushed to SQL via Sql.Expression; on SQLite they require in-memory filtering.
        if (_dialect.SupportsJsonbFilters == false)
        {
            if (criteria.HasDiskHealthIssue.HasValue || criteria.HasHardwareIssue.HasValue)
            {
                return true;
            }

            if (criteria.DiskMin.HasValue || criteria.DiskMax.HasValue)
            {
                return true;
            }
        }

        // Sorting by disk requires JSONB lateral join; only available on PostgreSQL.
        string sortLower = criteria.SortBy?.ToLowerInvariant() ?? "name";
        if ((sortLower == "disk") && (_dialect.SupportsJsonbSort == false))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Fast path: count, sort, and paginate at the SQL level, then resolve Redis and
    /// JSONB data only for the paged subset.
    /// </summary>
    private async Task<PaginatedResponse<FleetMachineDto>> SearchSqlPaginatedAsync(
        DatabaseContext db,
        MachineSearchCriteria criteria,
        int tenantId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        IQueryable<SearchScalarRow> query = BuildBaseQuery(db, tenantId, criteria);

        // Count at SQL level.
        int totalCount = await query.CountAsync(ct);

        // Sort at SQL level.
        IOrderedQueryable<SearchScalarRow> sorted = ApplySqlSort(query, criteria.SortBy, criteria.SortDir);

        // Paginate at SQL level — only the requested page is loaded into memory.
        List<SearchScalarRow> pagedRows = await sorted
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        // Resolve Redis only for the paged subset.
        List<long> pagedIds = pagedRows.Select(r => r.Id).ToList();
        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);
        Dictionary<long, bool> onlineMap = await _pingService.AreOnlineAsync(pagedIds, onlineThreshold);
        Dictionary<long, DateTimeOffset?> lastPingMap = await _pingService.GetLastPingsAsync(pagedIds);

        // Build DTOs for the paged subset only.
        List<FleetMachineDto> pagedDtos = BuildDtos(pagedRows, onlineMap, lastPingMap);

        // Enrich the paged subset with JSONB data for accurate display.
        EnrichWithJsonbData(pagedDtos, pagedRows, onlineMap);

        return new PaginatedResponse<FleetMachineDto>
        {
            Items = pagedDtos,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
        };
    }

    /// <summary>
    /// Slow path: loads all matching rows, resolves Redis for all of them, applies
    /// in-memory filters (health, last seen, disk, hardware), then paginates.
    /// Used when criteria include filters that cannot be evaluated at the SQL level.
    /// </summary>
    private async Task<PaginatedResponse<FleetMachineDto>> SearchFullScanAsync(
        DatabaseContext db,
        MachineSearchCriteria criteria,
        int tenantId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        IQueryable<SearchScalarRow> query = BuildBaseQuery(db, tenantId, criteria);

        // Load all matching scalar rows.
        List<SearchScalarRow> scalarRows = await query.ToListAsync(ct);

        // Resolve online status and last ping from Redis for all rows.
        List<long> machineIds = scalarRows.Select(r => r.Id).ToList();
        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);
        Dictionary<long, bool> onlineMap = await _pingService.AreOnlineAsync(machineIds, onlineThreshold);
        Dictionary<long, DateTimeOffset?> lastPingMap = await _pingService.GetLastPingsAsync(machineIds);

        // Build DTOs with computed health status.
        List<FleetMachineDto> allDtos = BuildDtos(scalarRows, onlineMap, lastPingMap);

        // Apply in-memory filters that require Redis data (health status, last seen).
        IEnumerable<FleetMachineDto> filtered = ApplyInMemoryFilters(allDtos, criteria);

        // If disk/hardware filters are active, enrich all filtered rows with JSONB
        // data so we can apply those filters before pagination.
        bool needsJsonbFilters = criteria.HasDiskHealthIssue.HasValue ||
            criteria.HasHardwareIssue.HasValue ||
            criteria.DiskMin.HasValue || criteria.DiskMax.HasValue;

        List<FleetMachineDto> filteredList = filtered.ToList();

        if (needsJsonbFilters)
        {
            EnrichWithJsonbData(filteredList, scalarRows, onlineMap);
            filteredList = ApplyPostEnrichmentFilters(filteredList, criteria);
        }

        // Sort in-memory.
        List<FleetMachineDto> sortedList = ApplySort(filteredList, criteria.SortBy, criteria.SortDir);

        int totalCount = sortedList.Count;

        // Paginate.
        List<FleetMachineDto> pagedDtos = sortedList
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        // Enrich the paged subset with JSONB data if not already done above.
        if (needsJsonbFilters == false)
        {
            EnrichWithJsonbData(pagedDtos, scalarRows, onlineMap);
        }

        return new PaginatedResponse<FleetMachineDto>
        {
            Items = pagedDtos,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
        };
    }

    private static List<FleetMachineDto> BuildDtos(
        List<SearchScalarRow> rows,
        Dictionary<long, bool> onlineMap,
        Dictionary<long, DateTimeOffset?> lastPingMap)
    {
        List<FleetMachineDto> dtos = new(rows.Count);

        foreach (SearchScalarRow row in rows)
        {
            bool isOnline = onlineMap.GetValueOrDefault(row.Id, false);
            DateTimeOffset? lastPing = row.StateLastPingAt ?? lastPingMap.GetValueOrDefault(row.Id);

            MachineHealthStatus health;
            if (row.StateHealthStatus.HasValue)
            {
                health = (MachineHealthStatus)row.StateHealthStatus.Value;
            }
            else
            {
                health = ComputeScalarHealth(
                    isOnline, row.StateCpuUsagePercent, row.StateMemoryUsagePercent, row.StateFailedServices);
            }

            dtos.Add(new FleetMachineDto
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

        return dtos;
    }

    private IQueryable<SearchScalarRow> BuildBaseQuery(
        DatabaseContext db, int tenantId, MachineSearchCriteria criteria)
    {
        IQueryable<SearchScalarRow> query =
            from m in db.Machines
            join s in db.MachineStates on m.Id equals s.MachineId into stateJoin
            from s in stateJoin.DefaultIfEmpty()
            where m.TenantId == tenantId && m.IsDeleted == false
            select new SearchScalarRow
            {
                Id = m.Id,
                Name = m.Name,
                OperatingSystem = m.OperatingSystem,
                MachineType = m.MachineType,
                StateHostname = s != null ? s.Hostname : null,
                StateIpAddresses = s != null ? s.IpAddresses : null,
                StateHardwareModel = s != null ? s.HardwareModel : null,
                StateCpuUsagePercent = s != null ? s.CpuUsagePercent : (int?)null,
                StateMemoryUsagePercent = s != null ? s.MemoryUsagePercent : (int?)null,
                StatePendingUpdates = s != null ? s.PendingUpdates : (int?)null,
                StateSecurityUpdates = s != null ? s.SecurityUpdates : (int?)null,
                StateFailedServices = s != null ? s.FailedServices : (int?)null,
                StateTotalServices = s != null ? s.TotalServices : (int?)null,
                StateDiskUsages = s != null ? s.DiskUsages : null,
                StateHardwareHealth = s != null ? s.HardwareHealth : null,
                StateHealthStatus = s != null ? s.HealthStatus : (short?)null,
                StateLastPingAt = s != null ? s.LastPingAt : (DateTimeOffset?)null,
            };

        // Apply SQL-level text search filter.
        if (string.IsNullOrWhiteSpace(criteria.Search) == false)
        {
            string searchLower = criteria.Search.ToLowerInvariant();
            query = query.Where(r =>
                r.Name.ToLower().Contains(searchLower) ||
                (r.StateHostname != null && r.StateHostname.ToLower().Contains(searchLower)) ||
                (r.StateHardwareModel != null && r.StateHardwareModel.ToLower().Contains(searchLower)));
        }

        // Apply SQL-level OS filter.
        if (string.IsNullOrWhiteSpace(criteria.Os) == false &&
            Enum.TryParse<OperatingSystems>(criteria.Os, true, out OperatingSystems osEnum))
        {
            query = query.Where(r => r.OperatingSystem == osEnum);
        }

        // Apply SQL-level machine type filter.
        if (string.IsNullOrWhiteSpace(criteria.Type) == false &&
            Enum.TryParse<MachineTypes>(criteria.Type, true, out MachineTypes typeEnum))
        {
            query = query.Where(r => r.MachineType == typeEnum);
        }

        // Apply SQL-level CPU range filters.
        if (criteria.CpuMin.HasValue)
        {
            int cpuMin = criteria.CpuMin.Value;
            query = query.Where(r => r.StateCpuUsagePercent != null && r.StateCpuUsagePercent >= cpuMin);
        }

        if (criteria.CpuMax.HasValue)
        {
            int cpuMax = criteria.CpuMax.Value;
            query = query.Where(r => r.StateCpuUsagePercent != null && r.StateCpuUsagePercent <= cpuMax);
        }

        // Apply SQL-level memory range filters.
        if (criteria.MemoryMin.HasValue)
        {
            int memMin = criteria.MemoryMin.Value;
            query = query.Where(r => r.StateMemoryUsagePercent != null && r.StateMemoryUsagePercent >= memMin);
        }

        if (criteria.MemoryMax.HasValue)
        {
            int memMax = criteria.MemoryMax.Value;
            query = query.Where(r => r.StateMemoryUsagePercent != null && r.StateMemoryUsagePercent <= memMax);
        }

        // Apply SQL-level threshold filters.
        if (criteria.PendingUpdatesMin.HasValue)
        {
            int pendingMin = criteria.PendingUpdatesMin.Value;
            query = query.Where(r => r.StatePendingUpdates != null && r.StatePendingUpdates >= pendingMin);
        }

        if (criteria.SecurityUpdatesMin.HasValue)
        {
            int securityMin = criteria.SecurityUpdatesMin.Value;
            query = query.Where(r => r.StateSecurityUpdates != null && r.StateSecurityUpdates >= securityMin);
        }

        if (criteria.FailedServicesMin.HasValue)
        {
            int failedMin = criteria.FailedServicesMin.Value;
            query = query.Where(r => r.StateFailedServices != null && r.StateFailedServices >= failedMin);
        }

        // Apply JSONB filters via custom SQL expressions (PostgreSQL only).
        if (_dialect.SupportsJsonbFilters)
        {
            if (criteria.DiskMin.HasValue)
            {
                int diskMin = criteria.DiskMin.Value;
                query = query.Where(r => JsonbFilterExpressions.HasDiskUsageAbove(r.StateDiskUsages, diskMin));
            }

            if (criteria.DiskMax.HasValue)
            {
                int diskMax = criteria.DiskMax.Value;
                query = query.Where(r => JsonbFilterExpressions.AllDiskUsageAtOrBelow(r.StateDiskUsages, diskMax));
            }

            if (criteria.HasDiskHealthIssue.HasValue)
            {
                if (criteria.HasDiskHealthIssue.Value == true)
                {
                    query = query.Where(r => JsonbFilterExpressions.HasFailedDiskSmart(r.StateHardwareHealth));
                }
                else
                {
                    query = query.Where(r => JsonbFilterExpressions.AllDiskSmartHealthy(r.StateHardwareHealth));
                }
            }

            if (criteria.HasHardwareIssue.HasValue)
            {
                if (criteria.HasHardwareIssue.Value == true)
                {
                    query = query.Where(r => JsonbFilterExpressions.HasHardwareIssue(r.StateHardwareHealth));
                }
                else
                {
                    query = query.Where(r => JsonbFilterExpressions.NoHardwareIssues(r.StateHardwareHealth));
                }
            }
        }

        // Apply SQL-level health status filter using the pre-computed HealthStatus column.
        if (string.IsNullOrWhiteSpace(criteria.HealthStatus) == false)
        {
            HashSet<MachineHealthStatus> targetStatuses = ParseHealthStatuses(criteria.HealthStatus);
            if (targetStatuses.Count > 0)
            {
                List<short> statusValues = targetStatuses.Select(hs => (short)hs).ToList();
                query = query.Where(r => r.StateHealthStatus != null && statusValues.Contains(r.StateHealthStatus.Value));
            }
        }

        // Apply SQL-level last seen time range filters using the LastPingAt column.
        if (criteria.LastSeenAfter.HasValue)
        {
            DateTimeOffset after = criteria.LastSeenAfter.Value;
            query = query.Where(r => r.StateLastPingAt != null && r.StateLastPingAt >= after);
        }

        if (criteria.LastSeenBefore.HasValue)
        {
            DateTimeOffset before = criteria.LastSeenBefore.Value;
            query = query.Where(r => r.StateLastPingAt != null && r.StateLastPingAt <= before);
        }

        return query;
    }

    /// <summary>
    /// Applies ORDER BY at the SQL level for columns that exist in the scalar projection.
    /// Supports status and disk sorting via pre-computed health column and JSONB expressions.
    /// </summary>
    private static IOrderedQueryable<SearchScalarRow> ApplySqlSort(
        IQueryable<SearchScalarRow> query, string? sortBy, string? sortDir)
    {
        bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);

        return sortBy?.ToLowerInvariant() switch
        {
            "status" => desc
                ? query.OrderByDescending(r => r.StateHealthStatus ?? 0)
                : query.OrderBy(r => r.StateHealthStatus ?? 0),
            "cpu" => desc
                ? query.OrderByDescending(r => r.StateCpuUsagePercent ?? 0)
                : query.OrderBy(r => r.StateCpuUsagePercent ?? 0),
            "memory" => desc
                ? query.OrderByDescending(r => r.StateMemoryUsagePercent ?? 0)
                : query.OrderBy(r => r.StateMemoryUsagePercent ?? 0),
            "disk" => desc
                ? query.OrderByDescending(r => JsonbFilterExpressions.MaxDiskUsagePercent(r.StateDiskUsages))
                : query.OrderBy(r => JsonbFilterExpressions.MaxDiskUsagePercent(r.StateDiskUsages)),
            _ => desc
                ? query.OrderByDescending(r => r.Name)
                : query.OrderBy(r => r.Name),
        };
    }

    private static IEnumerable<FleetMachineDto> ApplyInMemoryFilters(
        List<FleetMachineDto> dtos, MachineSearchCriteria criteria)
    {
        IEnumerable<FleetMachineDto> filtered = dtos;

        // Health status filter (comma-separated, e.g., "healthy,warning").
        if (string.IsNullOrWhiteSpace(criteria.HealthStatus) == false)
        {
            HashSet<MachineHealthStatus> targetStatuses = ParseHealthStatuses(criteria.HealthStatus);
            if (targetStatuses.Count > 0)
            {
                filtered = filtered.Where(m => targetStatuses.Contains(m.HealthStatus));
            }
        }

        // Last seen time range filters.
        if (criteria.LastSeenAfter.HasValue)
        {
            DateTimeOffset after = criteria.LastSeenAfter.Value;
            filtered = filtered.Where(m => m.LastPing.HasValue && m.LastPing.Value >= after);
        }

        if (criteria.LastSeenBefore.HasValue)
        {
            DateTimeOffset before = criteria.LastSeenBefore.Value;
            filtered = filtered.Where(m => m.LastPing.HasValue && m.LastPing.Value <= before);
        }

        return filtered;
    }

    private static List<FleetMachineDto> ApplyPostEnrichmentFilters(
        List<FleetMachineDto> dtos, MachineSearchCriteria criteria)
    {
        IEnumerable<FleetMachineDto> filtered = dtos;

        if (criteria.DiskMin.HasValue)
        {
            int diskMin = criteria.DiskMin.Value;
            filtered = filtered.Where(m => m.MaxDiskUsagePercent.HasValue && m.MaxDiskUsagePercent.Value >= diskMin);
        }

        if (criteria.DiskMax.HasValue)
        {
            int diskMax = criteria.DiskMax.Value;
            filtered = filtered.Where(m => m.MaxDiskUsagePercent.HasValue && m.MaxDiskUsagePercent.Value <= diskMax);
        }

        if (criteria.HasDiskHealthIssue.HasValue)
        {
            if (criteria.HasDiskHealthIssue.Value == true)
            {
                filtered = filtered.Where(m => m.HasDiskHealthIssue == true);
            }
            else
            {
                filtered = filtered.Where(m => m.HasDiskHealthIssue == false);
            }
        }

        if (criteria.HasHardwareIssue.HasValue)
        {
            if (criteria.HasHardwareIssue.Value == true)
            {
                filtered = filtered.Where(m => m.HasHardwareIssue == true);
            }
            else
            {
                filtered = filtered.Where(m => m.HasHardwareIssue == false);
            }
        }

        return filtered.ToList();
    }

    /// <summary>
    /// In-memory sort for the full-scan path. Supports all sort keys including
    /// status and disk which are not available at the SQL level.
    /// </summary>
    private static List<FleetMachineDto> ApplySort(IEnumerable<FleetMachineDto> dtos, string? sortBy, string? sortDir)
    {
        bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);

        return sortBy?.ToLowerInvariant() switch
        {
            "status" => desc
                ? dtos.OrderByDescending(m => m.HealthStatus).ToList()
                : dtos.OrderBy(m => m.HealthStatus).ToList(),
            "cpu" => desc
                ? dtos.OrderByDescending(m => m.CpuUsagePercent ?? 0).ToList()
                : dtos.OrderBy(m => m.CpuUsagePercent ?? 0).ToList(),
            "memory" => desc
                ? dtos.OrderByDescending(m => m.MemoryUsagePercent ?? 0).ToList()
                : dtos.OrderBy(m => m.MemoryUsagePercent ?? 0).ToList(),
            "disk" => desc
                ? dtos.OrderByDescending(m => m.MaxDiskUsagePercent ?? 0).ToList()
                : dtos.OrderBy(m => m.MaxDiskUsagePercent ?? 0).ToList(),
            _ => desc
                ? dtos.OrderByDescending(m => m.Name).ToList()
                : dtos.OrderBy(m => m.Name).ToList(),
        };
    }

    /// <summary>
    /// Enriches DTOs with JSONB data (disk usage, hardware health) using the already-loaded
    /// scalar row projections. Avoids a second database query by deserializing directly
    /// from the SearchScalarRow's StateDiskUsages and StateHardwareHealth strings.
    /// </summary>
    private static void EnrichWithJsonbData(
        List<FleetMachineDto> dtos,
        List<SearchScalarRow> scalarRows,
        Dictionary<long, bool> onlineMap)
    {
        Dictionary<long, SearchScalarRow> rowMap = scalarRows.ToDictionary(r => r.Id);

        foreach (FleetMachineDto dto in dtos)
        {
            if (rowMap.TryGetValue(dto.Id, out SearchScalarRow? row) == false)
            {
                continue;
            }

            if (row.StateDiskUsages is not null)
            {
                List<DiskUsageEntryDto>? disks = JsonSerializer.Deserialize<List<DiskUsageEntryDto>>(
                    row.StateDiskUsages, JsonDefaults.SnakeCase);
                if (disks is { Count: > 0 })
                {
                    dto.MaxDiskUsagePercent = disks.Max(d => d.UsagePercent);
                }
            }

            if (row.StateHardwareHealth is not null)
            {
                HardwareHealthPayload? hw = JsonSerializer.Deserialize<HardwareHealthPayload>(
                    row.StateHardwareHealth, JsonDefaults.SnakeCase);
                if (hw is not null)
                {
                    dto.HasDiskHealthIssue = hw.DiskSmart.Exists(d =>
                        string.Equals(d.HealthStatus, "FAILED", StringComparison.OrdinalIgnoreCase));
                    dto.HasHardwareIssue = hw.Fans.Exists(f => f.Rpm == 0) ||
                        hw.PowerSupplies.Exists(p =>
                            string.Equals(p.Status, "ok", StringComparison.OrdinalIgnoreCase) == false);
                }
            }

            // Recompute health with full JSONB data for enriched machines.
            bool isOnline = onlineMap.GetValueOrDefault(dto.Id, false);
            dto.HealthStatus = ComputeEnrichedHealth(isOnline, row, dto);
        }
    }

    /// <summary>
    /// Computes health status using both scalar and JSONB data from the search scalar row.
    /// </summary>
    private static MachineHealthStatus ComputeEnrichedHealth(
        bool isOnline, SearchScalarRow row, FleetMachineDto dto)
    {
        if (isOnline == false)
        {
            return MachineHealthStatus.Offline;
        }

        // Critical checks.
        if ((row.StateCpuUsagePercent >= 95) || (row.StateMemoryUsagePercent >= 95) ||
            ((row.StateFailedServices ?? 0) > 0))
        {
            return MachineHealthStatus.Critical;
        }

        if ((dto.MaxDiskUsagePercent >= 95) || (dto.HasDiskHealthIssue == true) || (dto.HasHardwareIssue == true))
        {
            return MachineHealthStatus.Critical;
        }

        // Warning checks.
        if ((row.StateCpuUsagePercent >= 80) || (row.StateMemoryUsagePercent >= 80))
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

    private static MachineHealthStatus ComputeScalarHealth(
        bool isOnline, int? cpuPercent, int? memPercent, int? failedServices)
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
