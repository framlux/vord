// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Dashboard;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Services.Core.Telemetry;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Reads MachineStateSummary cache and maps to fleet/detail DTOs.
/// </summary>
public sealed class MachineStateService : IMachineStateService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly QuotaPauseMetrics _quotaPauseMetrics;
    private readonly ILogger<MachineStateService> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineStateService"/> class.
    /// </summary>
    public MachineStateService(IServiceScopeFactory scopeFactory, QuotaPauseMetrics quotaPauseMetrics, ILogger<MachineStateService> logger)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(quotaPauseMetrics);
        ArgumentNullException.ThrowIfNull(logger);

        _scopeFactory = scopeFactory;
        _quotaPauseMetrics = quotaPauseMetrics;
        _logger = logger;
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

        // Asked once so the summary counts, the filter and every row agree on which hosts are paused.
        IQuotaPauseService quotaPause = scope.ServiceProvider.GetRequiredService<IQuotaPauseService>();
        QuotaCutoff cutoff = await QuotaCutoffDisplay.GetOrNoneAsync(quotaPause, _quotaPauseMetrics, _logger, tenantId.Value, ct);

        // Step 1: SQL-level summary aggregation using pre-computed HealthStatus. Paused hosts come
        // back only as a count: their stored health is frozen at the last report, so it must not
        // reach a health bucket.
        (List<(short HealthStatus, int Count)> healthCounts, int totalSecurityUpdates, int pausedCount) =
            await machineStateRepo.GetFleetHealthAggregationAsync(tenantId.Value, cutoff.Kind, cutoff.CutoffId, ct);

        int activeMachines = healthCounts.Sum(h => h.Count);
        int warningCount = healthCounts.Where(h => h.HealthStatus == 1).Sum(h => h.Count);
        int criticalCount = healthCounts.Where(h => h.HealthStatus == 2).Sum(h => h.Count);
        int offlineCount = healthCounts.Where(h => h.HealthStatus == 3).Sum(h => h.Count);

        FleetSummaryDto summary = new()
        {
            TotalMachines = activeMachines + pausedCount,
            OnlineMachines = activeMachines - offlineCount,
            PausedCount = pausedCount,
            OfflineCount = offlineCount,
            WarningCount = warningCount,
            CriticalCount = criticalCount,
            SecurityUpdates = totalSecurityUpdates,
        };

        // Step 2: SQL-level filtering, sorting, and pagination via the fleet search pipeline.
        // The dashboard's named status filter maps onto the search path's HealthStatusValues, except
        // "paused", which is not a health and filters on the cutoff instead; an unrecognized value
        // applies no filter, matching the previous behavior.
        bool desc = string.Equals(sortDir, "desc", StringComparison.OrdinalIgnoreCase);
        List<short>? statusValues = statusFilter?.ToLowerInvariant() switch
        {
            "healthy" => [0],
            "warning" => [1],
            "critical" => [2],
            "offline" => [3],
            _ => null,
        };
        bool pausedOnly = string.Equals(statusFilter, "paused", StringComparison.OrdinalIgnoreCase);
        FleetSearchParameters parameters = new()
        {
            Search = search,
            HealthStatusValues = statusValues,
            PausedAfterId = cutoff.PausedAfterId,
            AllPaused = cutoff.AllPaused,
            Paused = pausedOnly ? true : null,
            SortBy = sortBy,
            SortDescending = desc,
            Skip = (page - 1) * pageSize,
            Take = pageSize,
        };
        (List<FleetMachineRow> pagedRows, int totalCount) = await machineStateRepo.SearchFleetMachinesAsync(
            tenantId.Value, parameters, ct);

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
            IsOnline = MachineLiveness.IsOnline(row.HealthStatus),
            IsPaused = row.IsPaused,
            LastPing = MachineLiveness.LastSeen(row.LastSeenAt, row.LastHeartbeatAt),
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

        Machine? machine = await machineRepo.GetActiveMachineByIdAsync(machineId, tenantId.Value, ct);

        if (machine is null)
        {
            return null;
        }

        IQuotaPauseService quotaPause = scope.ServiceProvider.GetRequiredService<IQuotaPauseService>();
        QuotaCutoff cutoff = await QuotaCutoffDisplay.GetOrNoneAsync(quotaPause, _quotaPauseMetrics, _logger, tenantId.Value, ct);

        // The position the machine limit is counted from, so a paused host can say how far past the
        // limit it sits. Counted here rather than carried on the list rows, which would have to rank
        // every machine they show.
        int activeRank = await machineRepo.GetRegisteredRankAsync(tenantId.Value, machineId, ct);

        MachineStateSummary? state = await machineStateRepo.GetSummaryForMachineAsync(machineId, ct);

        bool isOnline = MachineLiveness.IsOnline(state);
        DateTimeOffset? lastPing = MachineLiveness.LastSeen(state?.LastSeenAt, state?.LastHeartbeatAt);

        // Fetch latest raw telemetry per type for this machine.
        Dictionary<short, MachineTelemetry> latestByType = await machineStateRepo.GetLatestTelemetryPerTypeAsync(machineId, 7, ct);

        SystemInfoPayload? systemInfo = DeserializePayload<SystemInfoPayload>(latestByType, TelemetryTypeIds.SystemInfo);
        OsVersionPayload? osVersion = DeserializePayload<OsVersionPayload>(latestByType, TelemetryTypeIds.OsVersion);
        CpuUsagePayload? cpuUsage = DeserializePayload<CpuUsagePayload>(latestByType, TelemetryTypeIds.CpuUsage);
        MemoryUsagePayload? memoryUsage = DeserializePayload<MemoryUsagePayload>(latestByType, TelemetryTypeIds.MemoryUsage);
        MemoryInfoPayload? memoryInfo = DeserializePayload<MemoryInfoPayload>(latestByType, TelemetryTypeIds.MemoryInfo);
        DiskUsagePayload? diskUsages = DeserializePayload<DiskUsagePayload>(latestByType, TelemetryTypeIds.DiskUsage);
        HardwareHealthPayload? hwHealth = DeserializePayload<HardwareHealthPayload>(latestByType, TelemetryTypeIds.HardwareHealth);
        PackageUpdatesPayload? packages = DeserializePayload<PackageUpdatesPayload>(latestByType, TelemetryTypeIds.PackageUpdates);
        ServiceStatusPayload? services = DeserializePayload<ServiceStatusPayload>(latestByType, TelemetryTypeIds.ServiceStatus);
        AgentVersionPayload? agentVersion = DeserializePayload<AgentVersionPayload>(latestByType, TelemetryTypeIds.AgentVersion);

        List<ServiceEntryDto> failedServices = services?.Services
            .Where(s => string.Equals(s.ActiveState, "failed", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        // Recent SSH sessions (last 20).
        List<MachineTelemetry> sshTelemetry = await machineStateRepo.GetRecentTelemetryAsync(machineId, TelemetryTypeIds.SshSessions, 20, ct);
        List<SshSessionPayload> sshSessions = sshTelemetry
            .Select(t => JsonSerializer.Deserialize<SshSessionPayload>(t.Payload, JsonDefaults.SnakeCase))
            .OfType<SshSessionPayload>()
            .ToList();

        // Health comes from the swept column, the same value the machine list, its health filter
        // and the alert evaluator read. A machine that has never reported telemetry has no summary
        // row, and the fleet query treats that absence as offline, so this does too.
        MachineHealthStatus health = state is not null
            ? (MachineHealthStatus)state.HealthStatus
            : MachineHealthStatus.Offline;

        return new MachineDetailDto
        {
            Id = machine.Id,
            Name = machine.Name,
            Hostname = state?.Hostname ?? systemInfo?.Hostname,
            IsOnline = isOnline,
            IsPaused = cutoff.IsPaused(machine.Id),
            ActiveRank = activeRank,
            LastPing = lastPing,
            HealthStatus = health,
            SystemInfo = systemInfo,
            OsVersion = osVersion,
            CpuUsage = cpuUsage,
            MemoryUsage = memoryUsage,
            MemoryInfo = memoryInfo,
            // Paired with the payload deliberately: the lookup window is seven days, so the swap and
            // available figures need their own receipt time rather than the machine's last-seen time.
            MemoryInfoReceivedAt = latestByType.TryGetValue(TelemetryTypeIds.MemoryInfo, out MachineTelemetry? memoryInfoRow)
                ? memoryInfoRow.ServerReceivedAt
                : null,
            DiskUsages = diskUsages,
            HardwareHealth = hwHealth,
            PackageUpdates = packages,
            FailedServices = failedServices,
            TotalServices = services?.Services.Count ?? 0,
            RecentSshSessions = sshSessions,
            TelemetryLastUpdated = state?.LastSeenAt,
            AgentVersion = NormalizeAgentVersion(agentVersion?.Version),
        };
    }

    /// <summary>
    /// Normalizes a reported agent version for display. A blank version is reported as null so the
    /// UI shows "not reported" rather than an empty value.
    /// </summary>
    /// <param name="version">The version string from the latest AgentVersion telemetry payload.</param>
    /// <returns>The trimmed version, or null when nothing usable was reported.</returns>
    internal static string? NormalizeAgentVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        return version.Trim();
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
