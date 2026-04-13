// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// Single systemd service entry.
/// </summary>
public sealed class ServiceEntryDto
{
    /// <summary>Service unit name.</summary>
    public string Unit { get; set; } = "";

    /// <summary>Load state.</summary>
    public string LoadState { get; set; } = "";

    /// <summary>Active state.</summary>
    public string ActiveState { get; set; } = "";

    /// <summary>Sub-state.</summary>
    public string SubState { get; set; } = "";

    /// <summary>Service description.</summary>
    public string Description { get; set; } = "";
}
