// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Billing;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Machines;
using Framlux.FleetManagement.Services.Core.Models.Dashboard;
using Framlux.FleetManagement.Services.Core.Observability;

namespace Framlux.FleetManagement.Services.Core.Handlers;

/// <summary>
/// Handles dashboard summary data retrieval.
/// </summary>
public sealed class DashboardHandler
{
    private readonly IMachineRepository _machineRepo;
    private readonly IMachineStateRepository _machineStateRepo;
    private readonly IQuotaPauseService _quotaPause;
    private readonly QuotaPauseMetrics _quotaPauseMetrics;

    /// <summary>
    /// Creates a new instance of the <see cref="DashboardHandler"/> class.
    /// </summary>
    public DashboardHandler(
        IMachineRepository machineRepo,
        IMachineStateRepository machineStateRepo,
        IQuotaPauseService quotaPause,
        QuotaPauseMetrics quotaPauseMetrics)
    {
        ArgumentNullException.ThrowIfNull(machineRepo);
        ArgumentNullException.ThrowIfNull(machineStateRepo);
        ArgumentNullException.ThrowIfNull(quotaPause);
        ArgumentNullException.ThrowIfNull(quotaPauseMetrics);

        _machineRepo = machineRepo;
        _machineStateRepo = machineStateRepo;
        _quotaPause = quotaPause;
        _quotaPauseMetrics = quotaPauseMetrics;
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

        // A paused host keeps its heartbeat and its frozen health, so by liveness alone it would
        // still count as online. It is counted as paused instead, and in neither online nor offline.
        QuotaCutoff cutoff = await QuotaCutoffDisplay.GetOrNoneAsync(_quotaPause, _quotaPauseMetrics, tenantId.Value, ct);
        int pausedCount = await QuotaCutoffDisplay.GetPausedCountOrZeroAsync(_quotaPause, _quotaPauseMetrics, tenantId.Value, ct);
        int onlineCount = summaries.Count(s => (cutoff.IsPaused(s.MachineId) == false) && MachineLiveness.IsOnline(s));

        DashboardSummaryDto dto = new()
        {
            TotalMachines = machines.Count,
            OnlineMachines = onlineCount,
            PausedMachines = pausedCount,
            PendingApprovals = 0,
        };

        return ServiceResult<DashboardSummaryDto>.Ok(dto);
    }
}
