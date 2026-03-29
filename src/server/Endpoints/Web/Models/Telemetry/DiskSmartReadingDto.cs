// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json.Serialization;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// SMART disk health reading.
/// </summary>
public sealed class DiskSmartReadingDto
{
    /// <summary>Device path.</summary>
    [JsonPropertyName("device")]
    public string Device { get; set; } = "";

    /// <summary>Disk model.</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    /// <summary>SMART health status (PASSED/FAILED).</summary>
    [JsonPropertyName("health_status")]
    public string HealthStatus { get; set; } = "";

    /// <summary>Disk temperature in Celsius.</summary>
    [JsonPropertyName("temperature_celsius")]
    public int TemperatureCelsius { get; set; }

    /// <summary>SSD wearout percentage (0-100).</summary>
    [JsonPropertyName("wearout_percent")]
    public int WearoutPercent { get; set; }

    /// <summary>Power-on hours.</summary>
    [JsonPropertyName("power_on_hours")]
    public long PowerOnHours { get; set; }
}
