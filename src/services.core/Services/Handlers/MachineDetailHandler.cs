// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Models.Machines;
using Framlux.FleetManagement.Services.Core.Observability;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles machine detail operations.
/// </summary>
public sealed class MachineDetailHandler
{
    private readonly IMachineRepository _machineRepo;
    private readonly IMachineStateRepository _machineStateRepo;
    private readonly IMachinePingService _pingService;
    private readonly IMachineStateService _stateService;
    private readonly IQuotaPauseService _quotaPause;
    private readonly QuotaPauseMetrics _quotaPauseMetrics;
    private readonly ILogger<MachineDetailHandler> _logger;

    /// <summary>
    /// Creates a new instance of the <see cref="MachineDetailHandler"/> class.
    /// </summary>
    public MachineDetailHandler(
        IMachineRepository machineRepo,
        IMachineStateRepository machineStateRepo,
        IMachinePingService pingService,
        IMachineStateService stateService,
        IQuotaPauseService quotaPause,
        QuotaPauseMetrics quotaPauseMetrics,
        ILogger<MachineDetailHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(machineStateRepo);
        ArgumentNullException.ThrowIfNull(pingService);
        ArgumentNullException.ThrowIfNull(stateService);
        ArgumentNullException.ThrowIfNull(quotaPause);
        ArgumentNullException.ThrowIfNull(quotaPauseMetrics);
        ArgumentNullException.ThrowIfNull(logger);

        _machineRepo = machineRepo;
        _machineStateRepo = machineStateRepo;
        _pingService = pingService;
        _stateService = stateService;
        _quotaPause = quotaPause;
        _quotaPauseMetrics = quotaPauseMetrics;
        _logger = logger;
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
        QuotaCutoff cutoff = await QuotaCutoffDisplay.GetOrNoneAsync(_quotaPause, _quotaPauseMetrics, _logger, tenantId.Value, ct);

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
            HealthStatus = MachineLiveness.Health(summary),
            IsPaused = cutoff.IsPaused(machine.Id),
            LastPing = lastPing,
            RegisteredOn = machine.RegisteredOn,
            IsDeleted = machine.IsDeleted,
            CommandsEnabled = AgentCapabilities.HasRemoteCommands(capabilities),
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
        MachineHealthStatus healthStatus = MachineLiveness.Health(summary);
        bool isOnline = MachineLiveness.IsOnline(summary);
        DateTimeOffset? lastPing = MachineLiveness.LastSeen(summary?.LastSeenAt, summary?.LastHeartbeatAt);
        QuotaCutoff cutoff = await QuotaCutoffDisplay.GetOrNoneAsync(_quotaPause, _quotaPauseMetrics, _logger, tenantId.Value, ct);

        MachineStatusDto dto = new()
        {
            IsOnline = isOnline,
            IsPaused = cutoff.IsPaused(machineId),
            LastPing = lastPing,
            CommandsEnabled = AgentCapabilities.HasRemoteCommands(capabilities),
            HealthStatus = healthStatus,
        };

        return ServiceResult<MachineStatusDto>.Ok(dto);
    }
}
