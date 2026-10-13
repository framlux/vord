// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Models;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Observability;
using Framlux.FleetManagement.Services.Core.Security;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles machine management operations.
/// </summary>
public sealed class MachineHandler
{
    private readonly IMachineRepository _machineRepo;
    private readonly IMachineStateRepository _machineStateRepo;
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IAuditLogRepository _auditLog;
    private readonly IMachineBillingSync _machineBillingSync;
    private readonly IApiKeyCacheInvalidator _apiKeyCacheInvalidator;
    private readonly QuotaCutoffInvalidator _quotaInvalidator;
    private readonly IQuotaPauseService _quotaPause;
    private readonly QuotaPauseMetrics _quotaPauseMetrics;
    private readonly ILogger<MachineHandler> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineHandler"/> class.
    /// </summary>
    public MachineHandler(
        IMachineRepository machineRepo,
        IMachineStateRepository machineStateRepo,
        IAlertRuleRepository alertRuleRepo,
        IDatabaseTransactionProvider transactionProvider,
        IAuditLogRepository auditLog,
        IMachineBillingSync machineBillingSync,
        IApiKeyCacheInvalidator apiKeyCacheInvalidator,
        QuotaCutoffInvalidator quotaInvalidator,
        IQuotaPauseService quotaPause,
        QuotaPauseMetrics quotaPauseMetrics,
        ILogger<MachineHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(machineStateRepo);
        ArgumentNullException.ThrowIfNull(alertRuleRepo);
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(machineBillingSync);
        ArgumentNullException.ThrowIfNull(apiKeyCacheInvalidator);
        ArgumentNullException.ThrowIfNull(quotaInvalidator);
        ArgumentNullException.ThrowIfNull(quotaPause);
        ArgumentNullException.ThrowIfNull(quotaPauseMetrics);
        ArgumentNullException.ThrowIfNull(logger);

        _machineRepo = machineRepo;
        _machineStateRepo = machineStateRepo;
        _alertRuleRepo = alertRuleRepo;
        _transactionProvider = transactionProvider;
        _auditLog = auditLog;
        _machineBillingSync = machineBillingSync;
        _apiKeyCacheInvalidator = apiKeyCacheInvalidator;
        _quotaInvalidator = quotaInvalidator;
        _quotaPause = quotaPause;
        _quotaPauseMetrics = quotaPauseMetrics;
        _logger = logger;
    }

    /// <summary>
    /// Soft-deletes a machine.
    /// </summary>
    /// <param name="machineId">The machine ID to delete.</param>
    /// <param name="tenantId">The tenant ID of the requesting user.</param>
    /// <param name="userId">The user performing the deletion.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the API response.</returns>
    public async Task<ServiceResult<ApiResponse<object>>> DeleteAsync(long machineId, int? tenantId, int userId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<ApiResponse<object>>.NotFound();
        }

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        // Soft-delete first: this is the tenant-scoped write that proves the caller owns the machine.
        // Only once ownership is established do we touch the id-keyed assignment rows, so a foreign
        // machine id has no side effects even before the transaction would roll back.
        string? deletedKeyHash = await _machineRepo.SoftDeleteMachineAsync(machineId, tenantId.Value, userId, ct);

        if (deletedKeyHash is null)
        {
            return ServiceResult<ApiResponse<object>>.NotFound();
        }

        int removedAssignments = await _alertRuleRepo.RemoveAllMachineAssignmentsAsync(machineId, tenantId.Value, ct);
        if (removedAssignments > 0)
        {
            _logger.LogInformation(
                "Removed {Count} alert rule assignments for machine {MachineId} during deletion",
                removedAssignments, machineId);
        }

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, userId, machineId,
            AuditAction.MachineDeleted, AuditResourceType.Machine,
            machineId.ToString(), null, null), ct);

        await transaction.CommitAsync(ct);

        // Deleting a host moves the tenant's cutoff: the next registered host takes its place among the
        // active ones. Marked once the delete has committed, so a rolled-back delete never evicts a
        // still-right entry; the clear itself waits until the key eviction below has run, so nothing the
        // clear could throw can leave the deleted machine's key authenticating.
        _quotaInvalidator.MarkPending(tenantId.Value);

        // Clear the deleted machine's cached auth result so its key stops authenticating immediately
        // rather than lingering until the cache TTL expires. Done after commit so a rolled-back
        // delete never evicts a still-valid cache entry.
        if (string.IsNullOrEmpty(deletedKeyHash) == false)
        {
            await _apiKeyCacheInvalidator.InvalidateByHashAsync(deletedKeyHash, ct);
        }

        _quotaInvalidator.DispatchPending();

        // Report the updated billable quantity after deletion (best effort) so Stripe reflects
        // the new machine count without waiting for the next heartbeat cycle
        await _machineBillingSync.ReportActiveMachineUsageAsync(tenantId.Value, ct);

        return ServiceResult<ApiResponse<object>>.Ok(ApiResponse<object>.Ok(new { }, "Machine deleted successfully"));
    }

    /// <summary>
    /// Updates a machine's editable metadata (name, description, location).
    /// </summary>
    /// <param name="machineId">The machine ID to update.</param>
    /// <param name="tenantId">The tenant ID of the requesting user.</param>
    /// <param name="userId">The user performing the update.</param>
    /// <param name="name">The new machine display name.</param>
    /// <param name="description">The new description (null to clear).</param>
    /// <param name="location">The new location (null to clear).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the updated machine DTO.</returns>
    public async Task<ServiceResult<ApiResponse<MachineDto>>> UpdateAsync(
        long machineId,
        int? tenantId,
        int userId,
        string name,
        string? description,
        string? location,
        CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<ApiResponse<MachineDto>>.NotFound();
        }

        string trimmedName = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            return ServiceResult<ApiResponse<MachineDto>>.BadRequest("Name is required.");
        }

        if (trimmedName.Length > 250)
        {
            return ServiceResult<ApiResponse<MachineDto>>.BadRequest("Name must be 250 characters or fewer.");
        }

        if (location is not null && location.Length > 250)
        {
            return ServiceResult<ApiResponse<MachineDto>>.BadRequest("Location must be 250 characters or fewer.");
        }

        Machine? machine = await _machineRepo.GetActiveMachineByIdAsync(machineId, tenantId.Value, ct);

        if (machine is null)
        {
            return ServiceResult<ApiResponse<MachineDto>>.NotFound();
        }

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        bool machineUpdated = await _machineRepo.UpdateMachineFieldsAsync(machineId, tenantId.Value, trimmedName, description, location, ct);

        if (machineUpdated == false)
        {
            return ServiceResult<ApiResponse<MachineDto>>.NotFound();
        }

        // A missing summary row is a legitimate state — a machine that has never reported telemetry
        // has no summary yet — so the false return is not treated as a not-found condition.
        await _machineStateRepo.UpdateSummaryNameAsync(machineId, tenantId.Value, trimmedName, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, userId, machineId,
            AuditAction.MachineUpdated, AuditResourceType.Machine,
            machineId.ToString(), new { Name = trimmedName, Description = description, Location = location }, null), ct);

        await transaction.CommitAsync(ct);

        machine.Name = trimmedName;
        machine.Description = description;
        machine.Location = location;

        MachineStateSummary? summary = await _machineStateRepo.GetSummaryForMachineAsync(machineId, ct);
        QuotaCutoff cutoff = await QuotaCutoffDisplay.GetOrNoneAsync(_quotaPause, _quotaPauseMetrics, _logger, tenantId.Value, ct);

        MachineDto dto = BuildMachineDto(machine, summary, cutoff.IsPaused(machine.Id));

        return ServiceResult<ApiResponse<MachineDto>>.Ok(
            ApiResponse<MachineDto>.Ok(dto, "Machine updated successfully"));
    }

    /// <summary>
    /// Returns a paginated, filtered, and sorted list of machines.
    /// </summary>
    /// <param name="page">The page number (1-based).</param>
    /// <param name="pageSize">The number of items per page.</param>
    /// <param name="tenantId">The tenant ID of the requesting user.</param>
    /// <param name="search">Optional search term for hostname/name.</param>
    /// <param name="osFilter">Optional OS filter.</param>
    /// <param name="typeFilter">Optional machine type filter.</param>
    /// <param name="statusFilter">
    /// Optional status filter: healthy, warning, critical or offline (the swept health the status badge shows),
    /// online (any health but offline), or paused. A paused machine matches only paused. An unrecognised value
    /// applies no filter.
    /// </param>
    /// <param name="sortBy">The field to sort by.</param>
    /// <param name="sortDir">The sort direction (asc/desc).</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the paginated machine list.</returns>
    public async Task<ServiceResult<PaginatedResponse<MachineDto>>> ListAsync(
        int page,
        int pageSize,
        int? tenantId,
        string? search,
        string? osFilter,
        string? typeFilter,
        string? statusFilter,
        string sortBy,
        string sortDir,
        CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<PaginatedResponse<MachineDto>>.Ok(new PaginatedResponse<MachineDto>
            {
                Items = [],
                Page = 1,
                PageSize = pageSize,
                TotalCount = 0,
            });
        }

        if (page < 1)
        {
            page = 1;
        }

        if ((pageSize < 1) || (pageSize > 100))
        {
            pageSize = 25;
        }

        OperatingSystems? parsedOs = null;
        if (string.IsNullOrWhiteSpace(osFilter) == false && Enum.TryParse<OperatingSystems>(osFilter, true, out OperatingSystems osEnum))
        {
            parsedOs = osEnum;
        }

        MachineTypes? parsedType = null;
        if (string.IsNullOrWhiteSpace(typeFilter) == false && Enum.TryParse<MachineTypes>(typeFilter, true, out MachineTypes typeEnum))
        {
            parsedType = typeEnum;
        }

        // Asked once per request so every row, and every filter below, sees the same cutoff.
        QuotaCutoff cutoff = await QuotaCutoffDisplay.GetOrNoneAsync(_quotaPause, _quotaPauseMetrics, _logger, tenantId.Value, ct);

        // A status is known only from each machine's summary row, so when filtering by one we must
        // resolve it for all matching machines before pagination for totalCount and page results to
        // be correct. A value that is not a recognised status applies no filter at all.
        MachineStatusFilter? parsedStatus = MachineStatusFilter.Parse(statusFilter);

        if (parsedStatus is not null)
        {
            // Load all matching machines and resolve their status from their summary rows.
            List<Machine> allMachines = await _machineRepo.ListActiveMachinesForTenantAsync(tenantId.Value, ct);

            // Apply search/OS/type filters in memory since we loaded all machines
            if (string.IsNullOrWhiteSpace(search) == false)
            {
                string searchLower = search.ToLowerInvariant();
                allMachines = allMachines.Where(m => m.Name.ToLower().Contains(searchLower)).ToList();
            }

            if (parsedOs.HasValue)
            {
                allMachines = allMachines.Where(m => m.OperatingSystem == parsedOs.Value).ToList();
            }

            if (parsedType.HasValue)
            {
                allMachines = allMachines.Where(m => m.MachineType == parsedType.Value).ToList();
            }

            // One batch lookup for the whole candidate set, in place of the batch Redis call this
            // replaced. The filter still runs in memory: pushing it into SQL is a separate change.
            List<long> allIds = allMachines.Select(m => m.Id).ToList();
            Dictionary<long, MachineStateSummary> summaryMap = await LoadSummaryMapAsync(allIds, ct);

            // A paused host keeps its heartbeat and its frozen health, so the swept health alone would
            // put it under a health it no longer has. The filter matches it only under Paused.
            List<Machine> filtered = allMachines
                .Where(m => parsedStatus.Matches(summaryMap.GetValueOrDefault(m.Id), cutoff.IsPaused(m.Id)))
                .ToList();

            int totalCount = filtered.Count;

            // Sort in-memory.
            List<Machine> sorted = ApplySort(filtered, sortBy, sortDir);

            // Paginate in-memory.
            List<Machine> paged = sorted
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            List<MachineDto> dtos = paged
                .Select(machine => BuildMachineDto(machine, summaryMap.GetValueOrDefault(machine.Id), cutoff.IsPaused(machine.Id)))
                .ToList();

            PaginatedResponse<MachineDto> response = new()
            {
                Items = dtos,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount,
            };

            return ServiceResult<PaginatedResponse<MachineDto>>.Ok(response);
        }
        else
        {
            int totalCount = await _machineRepo.CountActiveMachinesAsync(tenantId.Value, search, parsedOs, parsedType, ct);

            List<Machine> machines = await _machineRepo.QueryActiveMachinesAsync(
                tenantId.Value, search, parsedOs, parsedType,
                sortBy, sortDir, (page - 1) * pageSize, pageSize, ct);

            // One batch lookup for the page, in place of the batch Redis call this replaced.
            List<long> machineIds = machines.Select(m => m.Id).ToList();
            Dictionary<long, MachineStateSummary> summaryMap = await LoadSummaryMapAsync(machineIds, ct);

            List<MachineDto> dtos = machines
                .Select(machine => BuildMachineDto(machine, summaryMap.GetValueOrDefault(machine.Id), cutoff.IsPaused(machine.Id)))
                .ToList();

            PaginatedResponse<MachineDto> response = new()
            {
                Items = dtos,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount,
            };

            return ServiceResult<PaginatedResponse<MachineDto>>.Ok(response);
        }
    }

    /// <summary>
    /// Loads the summary rows for a set of machines, keyed by machine id. A machine with no row
    /// is simply absent, which every liveness derivation reads as offline.
    /// </summary>
    private async Task<Dictionary<long, MachineStateSummary>> LoadSummaryMapAsync(List<long> machineIds, CancellationToken ct)
    {
        List<MachineStateSummary> summaries = await _machineStateRepo.GetSummaryListByMachineIdsAsync(machineIds, ct);

        return summaries.ToDictionary(s => s.MachineId);
    }

    private static MachineDto BuildMachineDto(Machine machine, MachineStateSummary? summary, bool isPaused)
    {
        return new MachineDto
        {
            Id = machine.Id,
            Name = machine.Name,
            Description = machine.Description,
            Location = machine.Location,
            Hostname = summary?.Hostname ?? machine.Name,
            OperatingSystem = machine.OperatingSystem,
            MachineType = machine.MachineType,
            SerialNumber = machine.SerialNumber,
            AssetTag = machine.AssetTagNumber,
            IsOnline = MachineLiveness.IsOnline(summary),
            HealthStatus = MachineLiveness.Health(summary),
            IsPaused = isPaused,
            LastPing = MachineLiveness.LastSeen(summary?.LastSeenAt, summary?.LastHeartbeatAt),
            RegisteredOn = machine.RegisteredOn,
            IsDeleted = machine.IsDeleted,
        };
    }

    private static List<Machine> ApplySort(List<Machine> machines, string sortBy, string sortDir)
    {
        bool desc = sortDir == "desc";

        return sortBy?.ToLowerInvariant() switch
        {
            "type" => desc
                ? machines.OrderByDescending(m => m.MachineType).ToList()
                : machines.OrderBy(m => m.MachineType).ToList(),
            "registeredon" => desc
                ? machines.OrderByDescending(m => m.RegisteredOn).ToList()
                : machines.OrderBy(m => m.RegisteredOn).ToList(),
            _ => desc
                ? machines.OrderByDescending(m => m.Name).ToList()
                : machines.OrderBy(m => m.Name).ToList(),
        };
    }
}
