// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// SMART disk health reading.
/// </summary>
public sealed class DiskSmartReadingDto
{
    /// <summary>Device path.</summary>
    public string Device { get; set; } = "";

    /// <summary>Disk model.</summary>
    public string Model { get; set; } = "";

    /// <summary>SMART health status (PASSED/FAILED).</summary>
    public string HealthStatus { get; set; } = "";

    /// <summary>Disk temperature in Celsius.</summary>
    public int TemperatureCelsius { get; set; }

    /// <summary>SSD wearout percentage (0-100).</summary>
    public int WearoutPercent { get; set; }

    /// <summary>Power-on hours.</summary>
    public long PowerOnHours { get; set; }
}
