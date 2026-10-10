// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Dashboard;

/// <summary>
/// Fleet-wide summary statistics.
/// </summary>
public sealed class FleetSummaryDto
{
    /// <summary>Total approved machines, paused ones included.</summary>
    public int TotalMachines { get; set; }

    /// <summary>Machines that have pinged recently. Paused machines are never counted.</summary>
    public int OnlineMachines { get; set; }

    /// <summary>
    /// Machines paused for exceeding the tenant's machine limit. They are counted here and in no
    /// health or online bucket, whatever health they last reported.
    /// </summary>
    public int PausedCount { get; set; }

    /// <summary>Offline machine count.</summary>
    public int OfflineCount { get; set; }

    /// <summary>Machines in Warning state.</summary>
    public int WarningCount { get; set; }

    /// <summary>Machines in Critical state.</summary>
    public int CriticalCount { get; set; }

    /// <summary>Total pending security updates across the fleet, leaving out paused machines.</summary>
    public int SecurityUpdates { get; set; }
}
