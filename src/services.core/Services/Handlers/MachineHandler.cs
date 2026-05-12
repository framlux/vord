// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Models;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles machine management operations.
/// </summary>
public sealed class MachineHandler : IMachineHandler
{
    private readonly IMachineRepository _machineRepo;
    private readonly IMachineStateRepository _machineStateRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IAlertRuleRepository _alertRuleRepo;
    private readonly IDatabaseTransactionProvider _transactionProvider;
    private readonly IAuditLogRepository _auditLog;
    private readonly IMachinePingService _pingService;
    private readonly ServerConfigurationService _configService;
    private readonly IBillingApiClient _billingApiClient;
    private readonly ISubscriptionService _subscriptionService;
    private readonly ILogger<MachineHandler> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineHandler"/> class.
    /// </summary>
    public MachineHandler(
        IMachineRepository machineRepo,
        IMachineStateRepository machineStateRepo,
        ITenantRepository tenantRepo,
        IAlertRuleRepository alertRuleRepo,
        IDatabaseTransactionProvider transactionProvider,
        IAuditLogRepository auditLog,
        IMachinePingService pingService,
        ServerConfigurationService configService,
        IBillingApiClient billingApiClient,
        ISubscriptionService subscriptionService,
        ILogger<MachineHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(machineStateRepo);
        ArgumentNullException.ThrowIfNull(tenantRepo);
        ArgumentNullException.ThrowIfNull(alertRuleRepo);
        ArgumentNullException.ThrowIfNull(transactionProvider);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(pingService);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(billingApiClient);
        ArgumentNullException.ThrowIfNull(subscriptionService);
        ArgumentNullException.ThrowIfNull(logger);

        _machineRepo = machineRepo;
        _machineStateRepo = machineStateRepo;
        _tenantRepo = tenantRepo;
        _alertRuleRepo = alertRuleRepo;
        _transactionProvider = transactionProvider;
        _auditLog = auditLog;
        _pingService = pingService;
        _configService = configService;
        _billingApiClient = billingApiClient;
        _subscriptionService = subscriptionService;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<ApiResponse<object>>> DeleteAsync(long machineId, int? tenantId, int userId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<ApiResponse<object>>.NotFound();
        }

        using IDatabaseTransaction transaction = await _transactionProvider.BeginTransactionAsync(ct);

        // Remove alert rule machine assignments before deletion
        int removedAssignments = await _alertRuleRepo.RemoveAllMachineAssignmentsAsync(machineId, ct);
        if (removedAssignments > 0)
        {
            _logger.LogInformation(
                "Removed {Count} alert rule assignments for machine {MachineId} during deletion",
                removedAssignments, machineId);
        }

        int updated = await _machineRepo.SoftDeleteMachineAsync(machineId, tenantId.Value, userId, ct);

        if (updated == 0)
        {
            return ServiceResult<ApiResponse<object>>.NotFound();
        }

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, userId, machineId,
            AuditAction.MachineDeleted, AuditResourceType.Machine,
            machineId.ToString(), null, null), ct);

        await transaction.CommitAsync(ct);

        // Report usage to billing for metered billing after deletion (best effort)
        try
        {
            TenantSubscription? subscription = await _subscriptionService.GetSubscriptionForTenantAsync(tenantId.Value, ct);

            // Only report usage for paid tiers; Free tier has no Stripe subscription
            if ((subscription is not null) && (subscription.Tier != SubscriptionTier.Free))
            {
                Tenant? tenant = await _tenantRepo.GetTenantByIdAsync(tenantId.Value, ct);

                if (tenant is not null)
                {
                    int activeMachineCount = await _machineRepo.GetActiveMachineCountAsync(tenantId.Value, ct);
                    await _billingApiClient.ReportMachineUsageAsync(tenant.ExternalId, activeMachineCount, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to report machine usage to billing after deleting machine {MachineId} for tenant {TenantId}",
                machineId, tenantId.Value);
        }

        return ServiceResult<ApiResponse<object>>.Ok(ApiResponse<object>.Ok(new { }, "Machine deleted successfully"));
    }

    /// <inheritdoc/>
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

        await _machineRepo.UpdateMachineFieldsAsync(machineId, trimmedName, description, location, ct);

        await _machineStateRepo.UpdateSummaryNameAsync(machineId, trimmedName, ct);

        await _auditLog.InsertAuditLogAsync(AuditHelper.Create(
            tenantId, userId, machineId,
            AuditAction.MachineUpdated, AuditResourceType.Machine,
            machineId.ToString(), new { Name = trimmedName, Description = description, Location = location }, null), ct);

        await transaction.CommitAsync(ct);

        machine.Name = trimmedName;
        machine.Description = description;
        machine.Location = location;

        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);
        bool isOnline = await _pingService.IsOnlineAsync(machine.Id, onlineThreshold);
        DateTimeOffset? lastPing = await _pingService.GetLastPingAsync(machine.Id);

        MachineStateSummary? summary = await _machineStateRepo.GetSummaryForMachineAsync(machineId, ct);

        MachineDto dto = BuildMachineDto(machine, isOnline, lastPing, summary?.Hostname);

        return ServiceResult<ApiResponse<MachineDto>>.Ok(
            ApiResponse<MachineDto>.Ok(dto, "Machine updated successfully"));
    }

    /// <inheritdoc/>
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

        // When filtering by online/offline status, we must resolve status for all matching machines
        // before pagination so that totalCount and page results are correct.
        bool hasStatusFilter = string.IsNullOrWhiteSpace(statusFilter) == false &&
            (statusFilter.Equals("online", StringComparison.OrdinalIgnoreCase) ||
             statusFilter.Equals("offline", StringComparison.OrdinalIgnoreCase));

        if (hasStatusFilter)
        {
            // Load all matching machines and resolve online status via batch Redis call.
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

            List<long> allIds = allMachines.Select(m => m.Id).ToList();
            TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);
            Dictionary<long, bool> onlineMap = await _pingService.AreOnlineAsync(allIds, onlineThreshold);
            Dictionary<long, DateTimeOffset?> lastPingMap = await _pingService.GetLastPingsAsync(allIds);

            bool wantOnline = statusFilter!.Equals("online", StringComparison.OrdinalIgnoreCase);
            List<Machine> filtered = allMachines
                .Where(m => onlineMap.GetValueOrDefault(m.Id, false) == wantOnline)
                .ToList();

            int totalCount = filtered.Count;

            // Sort in-memory.
            List<Machine> sorted = ApplySort(filtered, sortBy, sortDir);

            // Paginate in-memory.
            List<Machine> paged = sorted
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            List<long> pagedIds = paged.Select(m => m.Id).ToList();
            Dictionary<long, string?> hostnameMap = await _machineStateRepo.GetHostnameMapAsync(pagedIds, ct);

            List<MachineDto> dtos = paged.Select(machine => BuildMachineDto(
                machine,
                onlineMap.GetValueOrDefault(machine.Id, false),
                lastPingMap.GetValueOrDefault(machine.Id),
                hostnameMap.GetValueOrDefault(machine.Id))).ToList();

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

            // Batch Redis calls instead of N+1 individual calls.
            List<long> machineIds = machines.Select(m => m.Id).ToList();
            TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);
            Dictionary<long, bool> onlineMap = await _pingService.AreOnlineAsync(machineIds, onlineThreshold);
            Dictionary<long, DateTimeOffset?> lastPingMap = await _pingService.GetLastPingsAsync(machineIds);

            Dictionary<long, string?> hostnameMap = await _machineStateRepo.GetHostnameMapAsync(machineIds, ct);

            List<MachineDto> dtos = machines.Select(machine => BuildMachineDto(
                machine,
                onlineMap.GetValueOrDefault(machine.Id, false),
                lastPingMap.GetValueOrDefault(machine.Id),
                hostnameMap.GetValueOrDefault(machine.Id))).ToList();

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

    private static MachineDto BuildMachineDto(Machine machine, bool isOnline, DateTimeOffset? lastPing, string? telemetryHostname = null)
    {
        return new MachineDto
        {
            Id = machine.Id,
            Name = machine.Name,
            Description = machine.Description,
            Location = machine.Location,
            Hostname = telemetryHostname ?? machine.Name,
            OperatingSystem = machine.OperatingSystem,
            MachineType = machine.MachineType,
            SerialNumber = machine.SerialNumber,
            AssetTag = machine.AssetTagNumber,
            IsOnline = isOnline,
            LastPing = lastPing,
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
