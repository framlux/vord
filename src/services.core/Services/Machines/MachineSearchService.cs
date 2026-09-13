// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Models;
using Framlux.FleetManagement.Services.Core.Models.Machines;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Searches machines using advanced filter criteria across scalar and telemetry data.
/// Uses a fast SQL-paginated path for all common queries, including health status and last seen
/// filters which use pre-computed database columns. Falls back to a load-all path only when
/// JSONB filters (disk, hardware) cannot be pushed to SQL on the current dialect (SQLite).
/// On PostgreSQL, all filters and sorts are handled at the SQL level.
/// </summary>
public sealed class MachineSearchService
{
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineSearchService"/> class.
    /// </summary>
    public MachineSearchService(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Searches machines using the provided criteria and returns a paginated result.
    /// </summary>
    /// <param name="criteria">The search criteria containing filters, pagination, and sort options.</param>
    /// <param name="tenantId">The tenant ID of the requesting user.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PaginatedResponse<FleetMachineDto>> SearchAsync(
        MachineSearchCriteria criteria,
        int? tenantId,
        CancellationToken ct)
    {
        int page = criteria.Page < 1 ? 1 : criteria.Page;
        // Unreachable from HTTP, where the endpoint refuses an out-of-range page size outright.
        // This is the in-process backstop for a caller that bypasses it, and it defers to the same
        // shared bound rather than restating one: a second copy of this rule is how the endpoint
        // and the service came to disagree in the first place.
        int pageSize = PaginationLimits.IsValidPageSize(criteria.PageSize)
            ? criteria.PageSize
            : PaginationLimits.DefaultPageSize;

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
    /// Resolves the machines a filter matches to their ids, for building a selection without
    /// transferring the machines themselves.
    /// </summary>
    /// <param name="criteria">The search criteria supplying the filters. Paging and sort are ignored.</param>
    /// <param name="tenantId">The tenant ID of the requesting user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching ids, the unbounded match count, and whether the cap withheld any.</returns>
    public async Task<MachineIdSelectionDto> SearchIdsAsync(
        MachineSearchCriteria criteria,
        int? tenantId,
        CancellationToken ct)
    {
        if (tenantId is null)
        {
            return new MachineIdSelectionDto();
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        IMachineStateRepository machineStateRepo = scope.ServiceProvider.GetRequiredService<IMachineStateRepository>();

        // Page and page size are irrelevant here, but BuildSearchParameters needs them to compute
        // Skip and Take, which the ids query then ignores. Pass values that cannot underflow.
        FleetSearchParameters searchParams = BuildSearchParameters(criteria, 1, 1);

        (List<long> ids, int totalCount) = await machineStateRepo.SearchFleetMachineIdsAsync(
            tenantId.Value, searchParams, MachineSelectionLimits.MaxSelectableIds, ct);

        return new MachineIdSelectionDto
        {
            Ids = ids,
            TotalCount = totalCount,
            Truncated = totalCount > ids.Count,
        };
    }

    /// <summary>
    /// Fast path: count, sort, and paginate at the SQL level, then build DTOs for the paged
    /// subset only.
    /// </summary>
    private static async Task<PaginatedResponse<FleetMachineDto>> SearchSqlPaginatedAsync(
        IMachineStateRepository machineStateRepo,
        MachineSearchCriteria criteria,
        int tenantId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        FleetSearchParameters searchParams = BuildSearchParameters(criteria, page, pageSize);
        (List<FleetMachineRow> pagedRows, int totalCount) = await machineStateRepo.SearchFleetMachinesAsync(tenantId, searchParams, ct);

        // Build DTOs for the paged subset only. Health status comes straight from the swept
        // column the filter, sort and count all ran against, so the rows a filter selected are
        // the rows the page displays with that status.
        List<FleetMachineDto> pagedDtos = BuildDtos(pagedRows);

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

    private static List<FleetMachineDto> BuildDtos(List<FleetMachineRow> rows)
    {
        List<FleetMachineDto> dtos = new(rows.Count);

        foreach (FleetMachineRow row in rows)
        {
            bool isOnline = MachineLiveness.IsOnline(row.HealthStatus);
            DateTimeOffset? lastPing = MachineLiveness.LastSeen(row.LastSeenAt, row.LastHeartbeatAt);
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
