// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Machines;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles machine detail operations.
/// </summary>
public sealed class MachineDetailHandler : IMachineDetailHandler
{
    private const ulong CapabilityRemoteCommands = 1UL;

    private readonly IMachineRepository _machineRepo;
    private readonly IMachineStateRepository _machineStateRepo;
    private readonly IMachinePingService _pingService;
    private readonly ServerConfigurationService _configService;
    private readonly IMachineStateService _stateService;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineDetailHandler"/> class.
    /// </summary>
    public MachineDetailHandler(
        IMachineRepository machineRepo,
        IMachineStateRepository machineStateRepo,
        IMachinePingService pingService,
        ServerConfigurationService configService,
        IMachineStateService stateService)
    {
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(machineStateRepo);
        ArgumentNullException.ThrowIfNull(pingService);
        ArgumentNullException.ThrowIfNull(configService);
        ArgumentNullException.ThrowIfNull(stateService);

        _machineRepo = machineRepo;
        _machineStateRepo = machineStateRepo;
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

        Machine? machine = await _machineRepo.GetActiveMachineByIdAsync(machineId, tenantId.Value, ct);

        if (machine is null)
        {
            return ServiceResult<MachineDto>.NotFound();
        }

        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);
        bool isOnline = await _pingService.IsOnlineAsync(machine.Id, onlineThreshold);
        DateTimeOffset? lastPing = await _pingService.GetLastPingAsync(machine.Id);
        ulong capabilities = await _pingService.GetAgentCapabilitiesAsync(machine.Id);

        MachineStateSummary? summary = await _machineStateRepo.GetSummaryForMachineAsync(machine.Id, ct);

        MachineDto dto = new()
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

        Machine? machine = await _machineRepo.GetActiveMachineByIdAsync(machineId, tenantId.Value, ct);

        if (machine is null)
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
}
