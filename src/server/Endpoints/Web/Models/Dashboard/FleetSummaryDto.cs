// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Dashboard;

/// <summary>
/// Fleet-wide summary statistics.
/// </summary>
public sealed class FleetSummaryDto
{
    /// <summary>Total approved machines.</summary>
    public int TotalMachines { get; set; }

    /// <summary>Machines that have pinged recently.</summary>
    public int OnlineMachines { get; set; }

    /// <summary>Offline machine count.</summary>
    public int OfflineCount { get; set; }

    /// <summary>Machines in Warning state.</summary>
    public int WarningCount { get; set; }

    /// <summary>Machines in Critical state.</summary>
    public int CriticalCount { get; set; }

    /// <summary>Total pending security updates across fleet.</summary>
    public int SecurityUpdates { get; set; }
}
