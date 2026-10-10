// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Dashboard;

/// <summary>
/// Dashboard summary data.
/// </summary>
public sealed class DashboardSummaryDto
{
    /// <summary>
    /// Total number of approved machines.
    /// </summary>
    public int TotalMachines { get; set; }

    /// <summary>
    /// Number of machines currently online. Paused machines are never counted.
    /// </summary>
    public int OnlineMachines { get; set; }

    /// <summary>
    /// Number of machines paused for exceeding the tenant's machine limit.
    /// </summary>
    public int PausedMachines { get; set; }

    /// <summary>
    /// Number of machines pending approval.
    /// </summary>
    public int PendingApprovals { get; set; }
}
