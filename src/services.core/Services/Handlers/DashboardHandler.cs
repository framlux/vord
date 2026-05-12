// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Models.Dashboard;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.ServerConfiguration;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles dashboard summary data retrieval.
/// </summary>
public sealed class DashboardHandler : IDashboardHandler
{
    private readonly IMachineRepository _machineRepo;
    private readonly IMachinePingService _pingService;
    private readonly ServerConfigurationService _configService;

    /// <summary>
    /// Creates a new instance of the <see cref="DashboardHandler"/> class.
    /// </summary>
    public DashboardHandler(IMachineRepository machineRepo, IMachinePingService pingService, ServerConfigurationService configService)
    {
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(pingService);
        ArgumentNullException.ThrowIfNull(configService);

        _machineRepo = machineRepo;
        _pingService = pingService;
        _configService = configService;
    }

    /// <inheritdoc/>
    public async Task<ServiceResult<DashboardSummaryDto>> GetSummaryAsync(int? tenantId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<DashboardSummaryDto>.Ok(new DashboardSummaryDto());
        }

        List<Machine> machines = await _machineRepo.ListActiveMachinesForTenantAsync(tenantId.Value, ct);

        List<long> machineIds = machines.Select(m => m.Id).ToList();
        TimeSpan onlineThreshold = await _configService.GetOnlineThresholdAsync(ct);
        Dictionary<long, bool> onlineMap = await _pingService.AreOnlineAsync(machineIds, onlineThreshold);
        int onlineCount = onlineMap.Count(kvp => kvp.Value);

        DashboardSummaryDto dto = new()
        {
            TotalMachines = machines.Count,
            OnlineMachines = onlineCount,
            PendingApprovals = 0,
        };

        return ServiceResult<DashboardSummaryDto>.Ok(dto);
    }
}
