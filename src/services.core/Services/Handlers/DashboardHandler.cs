// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Models.Dashboard;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles dashboard summary data retrieval.
/// </summary>
public sealed class DashboardHandler
{
    private readonly IMachineRepository _machineRepo;
    private readonly IMachineStateRepository _machineStateRepo;

    /// <summary>
    /// Creates a new instance of the <see cref="DashboardHandler"/> class.
    /// </summary>
    public DashboardHandler(IMachineRepository machineRepo, IMachineStateRepository machineStateRepo)
    {
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(machineStateRepo);

        _machineRepo = machineRepo;
        _machineStateRepo = machineStateRepo;
    }

    /// <summary>
    /// Gets the dashboard summary statistics.
    /// </summary>
    /// <param name="tenantId">The tenant ID of the requesting user.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>A service result containing the dashboard summary data.</returns>
    public async Task<ServiceResult<DashboardSummaryDto>> GetSummaryAsync(int? tenantId, CancellationToken ct)
    {
        if (tenantId is null)
        {
            return ServiceResult<DashboardSummaryDto>.Ok(new DashboardSummaryDto());
        }

        List<Machine> machines = await _machineRepo.ListActiveMachinesForTenantAsync(tenantId.Value, ct);

        // Counted from the swept summary rows, the same source the fleet list and the machine
        // detail page read. A machine with no summary row has never been heard from and counts
        // as offline, which is what every other read path does with that absence.
        List<long> machineIds = machines.Select(m => m.Id).ToList();
        List<MachineStateSummary> summaries = await _machineStateRepo.GetSummaryListByMachineIdsAsync(machineIds, ct);
        int onlineCount = summaries.Count(s => MachineLiveness.IsOnline(s));

        DashboardSummaryDto dto = new()
        {
            TotalMachines = machines.Count,
            OnlineMachines = onlineCount,
            PendingApprovals = 0,
        };

        return ServiceResult<DashboardSummaryDto>.Ok(dto);
    }
}
