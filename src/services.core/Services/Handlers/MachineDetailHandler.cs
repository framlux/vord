// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Models.Machines;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles machine detail operations.
/// </summary>
public sealed class MachineDetailHandler
{
    private const ulong CapabilityRemoteCommands = 1UL;

    private readonly IMachineRepository _machineRepo;
    private readonly IMachineStateRepository _machineStateRepo;
    private readonly IMachinePingService _pingService;
    private readonly IMachineStateService _stateService;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineDetailHandler"/> class.
    /// </summary>
    public MachineDetailHandler(
        IMachineRepository machineRepo,
        IMachineStateRepository machineStateRepo,
        IMachinePingService pingService,
        IMachineStateService stateService)
    {
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(machineStateRepo);
        ArgumentNullException.ThrowIfNull(pingService);
        ArgumentNullException.ThrowIfNull(stateService);

        _machineRepo = machineRepo;
        _machineStateRepo = machineStateRepo;
        _pingService = pingService;
        _stateService = stateService;
    }

    /// <summary>
    /// Gets the basic detail for a machine.
    /// </summary>
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

        ulong capabilities = await _pingService.GetAgentCapabilitiesAsync(machine.Id);

        MachineStateSummary? summary = await _machineStateRepo.GetSummaryForMachineAsync(machine.Id, ct);
        bool isOnline = MachineLiveness.IsOnline(summary);
        DateTimeOffset? lastPing = MachineLiveness.LastSeen(summary?.LastSeenAt, summary?.LastHeartbeatAt);

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

    /// <summary>
    /// Gets the full detail for a machine (delegates to IMachineStateService).
    /// </summary>
    public async Task<ServiceResult<MachineDetailDto>> GetFullDetailAsync(long machineId, int? tenantId, CancellationToken ct)
    {
        MachineDetailDto? detail = await _stateService.GetMachineDetailAsync(machineId, tenantId, ct);
        if (detail is null)
        {
            return ServiceResult<MachineDetailDto>.NotFound();
        }

        return ServiceResult<MachineDetailDto>.Ok(detail);
    }

    /// <summary>
    /// Gets the online/offline status for a machine.
    /// </summary>
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

        ulong capabilities = await _pingService.GetAgentCapabilitiesAsync(machineId);

        // Health and liveness both come from the swept column so this endpoint cannot report a
        // status that disagrees with the list the user reached the machine from. A machine with
        // no summary row has never been heard from, which the fleet query treats as offline.
        MachineStateSummary? summary = await _machineStateRepo.GetSummaryForMachineAsync(machineId, ct);
        MachineHealthStatus healthStatus = summary is not null
            ? (MachineHealthStatus)summary.HealthStatus
            : MachineHealthStatus.Offline;
        bool isOnline = MachineLiveness.IsOnline(summary);
        DateTimeOffset? lastPing = MachineLiveness.LastSeen(summary?.LastSeenAt, summary?.LastHeartbeatAt);

        MachineStatusDto dto = new()
        {
            IsOnline = isOnline,
            LastPing = lastPing,
            CommandsEnabled = (capabilities & CapabilityRemoteCommands) != 0,
            HealthStatus = healthStatus,
        };

        return ServiceResult<MachineStatusDto>.Ok(dto);
    }
}
