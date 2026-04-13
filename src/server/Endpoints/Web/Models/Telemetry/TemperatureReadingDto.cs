// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// Temperature sensor reading.
/// </summary>
public sealed class TemperatureReadingDto
{
    /// <summary>Sensor name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Temperature in Celsius.</summary>
    public double Celsius { get; set; }

    /// <summary>Sensor status.</summary>
    public string Status { get; set; } = "";
}
