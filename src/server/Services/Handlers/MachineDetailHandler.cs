// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Server.Endpoints.Web;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using LinqToDB;
using LinqToDB.Async;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles machine detail operations.
/// </summary>
public sealed class MachineDetailHandler : IMachineDetailHandler
{
    private const ulong CapabilityRemoteCommands = 1UL;

    private readonly DatabaseContext _db;
    private readonly IMachinePingService _pingService;
    private readonly ServerConfigurationService _configService;
    private readonly IMachineStateService _stateService;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineDetailHandler"/> class.
    /// </summary>
    public MachineDetailHandler(
        DatabaseContext db,
        IMachinePingService pingService,
        ServerConfigurationService configService,
        IMachineStateService stateService)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(pingService);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(stateService);

        _db = db;
        _pingService = pingService;
        _configService = configService;
        _stateService = stateService;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<MachineDto>> GetDetailAsync(long machineId, int? tenantId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<MachineDto>.NotFound();
        }

        Machine? machine = await _db.Machines
            .FirstOrDefaultAsync(m => m.Id == machineId && m.TenantId == tenantId.Value && m.IsDeleted == false, ct);

        if (machine is null)
        {
            return ServiceResult<MachineDto>.NotFound();
        }

        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);
        bool isOnline = await _pingService.IsOnlineAsync(machine.Id, onlineThreshold);
        DateTimeOffset? lastPing = await _pingService.GetLastPingAsync(machine.Id);
        ulong capabilities = await _pingService.GetAgentCapabilitiesAsync(machine.Id);

        MachineDto dto = new()
        {
            Id = machine.Id,
            Name = machine.Name,
            Description = machine.Description,
            Location = machine.Location,
            Hostname = machine.Name,
            OperatingSystem = machine.OperatingSystem,
            MachineType = machine.MachineType,
            SerialNumber = machine.SerialNumber,
            AssetTag = machine.AssetTagNumber,
            IsOnline = isOnline,
            LastPing = lastPing,
            RegisteredOn = machine.RegisteredOn,
            IsDeleted = machine.IsDeleted,
            CommandsEnabled = (capabilities & CapabilityRemoteCommands) != 0,
        };

        return ServiceResult<MachineDto>.Ok(dto);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<MachineDetailDto>> GetFullDetailAsync(long machineId, int? tenantId, CancellationToken ct)
    {
        MachineDetailDto? detail = await _stateService.GetMachineDetailAsync(machineId, tenantId, ct);
        if (detail is null)
        {
            return ServiceResult<MachineDetailDto>.NotFound();
        }

        return ServiceResult<MachineDetailDto>.Ok(detail);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<MachineStatusDto>> GetStatusAsync(long machineId, int? tenantId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<MachineStatusDto>.NotFound();
        }

        bool machineExists = await _db.Machines
            .AnyAsync(m => m.Id == machineId && m.TenantId == tenantId.Value && m.IsDeleted == false, ct);

        if (machineExists == false)
        {
            return ServiceResult<MachineStatusDto>.NotFound();
        }

        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);
        bool isOnline = await _pingService.IsOnlineAsync(machineId, onlineThreshold);
        DateTimeOffset? lastPing = await _pingService.GetLastPingAsync(machineId);
        ulong capabilities = await _pingService.GetAgentCapabilitiesAsync(machineId);

        MachineStatusDto dto = new()
        {
            IsOnline = isOnline,
            LastPing = lastPing,
            CommandsEnabled = (capabilities & CapabilityRemoteCommands) != 0,
        };

        return ServiceResult<MachineStatusDto>.Ok(dto);
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<PaginatedResponse<MachineCertificateDto>>> GetCertificatesAsync(
        long machineId, int? tenantId, int page, int pageSize, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<PaginatedResponse<MachineCertificateDto>>.NotFound();
        }

        if (page < 1)
        {
            page = 1;
        }

        if ((pageSize < 1) || (pageSize > 100))
        {
            pageSize = 25;
        }

        bool machineExists = await _db.Machines
            .AnyAsync(m => m.Id == machineId && m.TenantId == tenantId.Value && m.IsDeleted == false, ct);

        if (machineExists == false)
        {
            return ServiceResult<PaginatedResponse<MachineCertificateDto>>.NotFound();
        }

        IQueryable<MachineCertificate> query = _db.MachineCertificates
            .Where(c => c.MachineId == machineId)
            .OrderByDescending(c => c.IssuedAt);

        int totalCount = await query.CountAsync(ct);

        List<MachineCertificate> certs = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        List<MachineCertificateDto> dtos = certs.Select(c => new MachineCertificateDto
        {
            Id = c.Id,
            Thumbprint = c.Thumbprint,
            IssuedAt = c.IssuedAt,
            ExpiresAt = c.ExpiresAt,
            RevokedAt = c.RevokedAt,
            IsActive = c.RevokedAt == null && c.ExpiresAt > DateTimeOffset.UtcNow,
        }).ToList();

        PaginatedResponse<MachineCertificateDto> result = new()
        {
            Items = dtos,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
        };

        return ServiceResult<PaginatedResponse<MachineCertificateDto>>.Ok(result);
    }
}
