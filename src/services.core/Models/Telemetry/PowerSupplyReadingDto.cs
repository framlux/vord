// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Telemetry;

/// <summary>
/// Power supply reading.
/// </summary>
public sealed class PowerSupplyReadingDto
{
    /// <summary>PSU name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Power draw in watts.</summary>
    public int Watts { get; set; }

    /// <summary>PSU status.</summary>
    public string Status { get; set; } = "";
}
