// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Models.Machines;

namespace Framlux.FleetManagement.Services.Core.Models.Dashboard;

/// <summary>
/// Top-level fleet overview response.
/// </summary>
public sealed class FleetOverviewDto
{
    /// <summary>Fleet-wide summary counts.</summary>
    public required FleetSummaryDto Summary { get; set; }

    /// <summary>Per-machine state rows.</summary>
    public required List<FleetMachineDto> Machines { get; set; }
}
