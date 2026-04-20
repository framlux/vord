// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database;
using Framlux.FleetManagement.Server.Endpoints.Web.Models.Dashboard;
using Framlux.FleetManagement.Server.Services.Infrastructure;
using Framlux.FleetManagement.Server.Services.Machines;
using Framlux.FleetManagement.Server.Services.ServerConfiguration;
using LinqToDB.Async;
using LinqToDB;

namespace Framlux.FleetManagement.Server.Services.Handlers;

/// <summary>
/// Handles dashboard summary data retrieval.
/// </summary>
public sealed class DashboardHandler : IDashboardHandler
{
    private readonly DatabaseContext _db;
    private readonly IMachinePingService _pingService;
    private readonly ServerConfigurationService _configService;

    /// <summary>
    /// Creates a new instance of the <see cref="DashboardHandler"/> class.
    /// </summary>
    public DashboardHandler(DatabaseContext db, IMachinePingService pingService, ServerConfigurationService configService)
    {
        _db = db;
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

        List<Machine> machines = await _db.Machines
            .Where(m => m.TenantId == tenantId.Value && m.IsDeleted == false)
            .ToListAsync(ct);

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
