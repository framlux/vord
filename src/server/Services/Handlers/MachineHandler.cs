// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Services.Billing;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles machine management operations.
/// </summary>
public sealed class MachineHandler : IMachineHandler
{
    private readonly DatabaseContext _db;
    private readonly IMachinePingService _pingService;
    private readonly ServerConfigurationService _configService;
    private readonly IBillingApiClient _billingApiClient;
    private readonly ILogger<MachineHandler> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineHandler"/> class.
    /// </summary>
    public MachineHandler(
        DatabaseContext db,
        IMachinePingService pingService,
        ServerConfigurationService configService,
        IBillingApiClient billingApiClient,
        ILogger<MachineHandler> logger)
    {
        _db = db;
        _pingService = pingService;
        _configService = configService;
        _billingApiClient = billingApiClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<ApiResponse<object>>> DeleteAsync(long machineId, int? tenantId, int userId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<ApiResponse<object>>.NotFound();
        }

        using DataConnectionTransaction transaction = await _db.BeginTransactionAsync(ct);

        int updated = await _db.Machines
            .Where(m => m.Id == machineId && m.TenantId == tenantId.Value && m.IsDeleted == false)
            .Set(m => m.IsDeleted, true)
            .Set(m => m.DeletedOn, DateTimeOffset.UtcNow)
            .Set(m => m.DeletedByUserId, userId)
            .UpdateAsync(ct);

        if (updated == 0)
        {
            return ServiceResult<ApiResponse<object>>.NotFound();
        }

        await _db.InsertAsync(AuditHelper.Create(
            tenantId, userId, machineId,
            AuditAction.MachineDeleted, AuditResourceType.Machine,
            machineId.ToString(), null, null), token: ct);

        await transaction.CommitAsync(ct);

        // Sync the machine quantity with Stripe after deletion (fire-and-forget)
        try
        {
            Tenant? tenant = await _db.Tenants
                .Where(t => t.Id == tenantId.Value)
                .FirstOrDefaultAsync(ct);

            if (tenant is not null)
            {
                int activeMachineCount = await _db.Machines
                    .Where(m => m.TenantId == tenantId.Value && m.IsDeleted == false)
                    .CountAsync(ct);

                await _billingApiClient.UpdateQuantityAsync(tenant.ExternalId, activeMachineCount, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to sync machine quantity with Stripe after deleting machine {MachineId} for tenant {TenantId}",
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

        Machine? machine = await _db.Machines
            .FirstOrDefaultAsync(m => m.Id == machineId && m.TenantId == tenantId.Value && m.IsDeleted == false, ct);

        if (machine is null)
        {
            return ServiceResult<ApiResponse<MachineDto>>.NotFound();
        }

        using DataConnectionTransaction transaction = await _db.BeginTransactionAsync(ct);

        await _db.Machines
            .Where(m => m.Id == machineId)
            .Set(m => m.Name, trimmedName)
            .Set(m => m.Description, description)
            .Set(m => m.Location, location)
            .UpdateAsync(ct);

        await _db.MachineStateSummaries
            .Where(s => s.MachineId == machineId)
            .Set(s => s.Name, trimmedName)
            .UpdateAsync(ct);

        await _db.InsertAsync(AuditHelper.Create(
            tenantId, userId, machineId,
            AuditAction.MachineUpdated, AuditResourceType.Machine,
            machineId.ToString(), new { Name = trimmedName, Description = description, Location = location }, null), token: ct);

        await transaction.CommitAsync(ct);

        machine.Name = trimmedName;
        machine.Description = description;
        machine.Location = location;

        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);
        bool isOnline = await _pingService.IsOnlineAsync(machine.Id, onlineThreshold);
        DateTimeOffset? lastPing = await _pingService.GetLastPingAsync(machine.Id);

        MachineStateSummary? summary = await _db.MachineStateSummaries
            .FirstOrDefaultAsync(s => s.MachineId == machineId, ct);

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

        IQueryable<Machine> query = _db.Machines
            .Where(m => m.TenantId == tenantId.Value && m.IsDeleted == false);

        if (string.IsNullOrWhiteSpace(search) == false)
        {
            string searchLower = search.ToLowerInvariant();
            query = query.Where(m => m.Name.ToLower().Contains(searchLower));
        }

        if (string.IsNullOrWhiteSpace(osFilter) == false && Enum.TryParse<OperatingSystems>(osFilter, true, out OperatingSystems osEnum))
        {
            query = query.Where(m => m.OperatingSystem == osEnum);
        }

        if (string.IsNullOrWhiteSpace(typeFilter) == false && Enum.TryParse<MachineTypes>(typeFilter, true, out MachineTypes typeEnum))
        {
            query = query.Where(m => m.MachineType == typeEnum);
        }

        // When filtering by online/offline status, we must resolve status for all matching machines
        // before pagination so that totalCount and page results are correct.
        bool hasStatusFilter = string.IsNullOrWhiteSpace(statusFilter) == false &&
            (statusFilter.Equals("online", StringComparison.OrdinalIgnoreCase) ||
             statusFilter.Equals("offline", StringComparison.OrdinalIgnoreCase));

        if (hasStatusFilter)
        {
            // Load all matching machine IDs and resolve online status via batch Redis call.
            List<Machine> allMachines = await query.ToListAsync(ct);
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
            Dictionary<long, string?> hostnameMap = await LoadHostnameMapAsync(pagedIds, ct);

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
            int totalCount = await query.CountAsync(ct);

            IOrderedQueryable<Machine> orderedQuery = sortBy?.ToLowerInvariant() switch
            {
                "type" => sortDir == "desc"
                    ? query.OrderByDescending(m => m.MachineType)
                    : query.OrderBy(m => m.MachineType),
                "registeredon" => sortDir == "desc"
                    ? query.OrderByDescending(m => m.RegisteredOn)
                    : query.OrderBy(m => m.RegisteredOn),
                _ => sortDir == "desc"
                    ? query.OrderByDescending(m => m.Name)
                    : query.OrderBy(m => m.Name),
            };

            List<Machine> machines = await orderedQuery
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            // Batch Redis calls instead of N+1 individual calls.
            List<long> machineIds = machines.Select(m => m.Id).ToList();
            TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);
            Dictionary<long, bool> onlineMap = await _pingService.AreOnlineAsync(machineIds, onlineThreshold);
            Dictionary<long, DateTimeOffset?> lastPingMap = await _pingService.GetLastPingsAsync(machineIds);

            Dictionary<long, string?> hostnameMap = await LoadHostnameMapAsync(machineIds, ct);

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

    private async Task<Dictionary<long, string?>> LoadHostnameMapAsync(List<long> machineIds, CancellationToken ct)
    {
        if (machineIds.Count == 0)
        {
            return new Dictionary<long, string?>();
        }

        return await _db.MachineStateSummaries
            .Where(s => machineIds.Contains(s.MachineId))
            .ToDictionaryAsync(s => s.MachineId, s => s.Hostname, ct);
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
