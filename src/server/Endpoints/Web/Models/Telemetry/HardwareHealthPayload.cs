// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json.Serialization;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// Hardware health telemetry payload (type=10) — structured categories.
/// </summary>
public sealed class HardwareHealthPayload
{
    /// <summary>Fan sensor readings.</summary>
    [JsonPropertyName("fans")]
    public List<FanReadingDto> Fans { get; set; } = [];

    /// <summary>Power supply readings.</summary>
    [JsonPropertyName("power_supplies")]
    public List<PowerSupplyReadingDto> PowerSupplies { get; set; } = [];

    /// <summary>Temperature sensor readings.</summary>
    [JsonPropertyName("temperatures")]
    public List<TemperatureReadingDto> Temperatures { get; set; } = [];

    /// <summary>SMART disk health readings.</summary>
    [JsonPropertyName("disk_smart")]
    public List<DiskSmartReadingDto> DiskSmart { get; set; } = [];

    /// <summary>BMC firmware version.</summary>
    [JsonPropertyName("bmc_firmware_version")]
    public string BmcFirmwareVersion { get; set; } = "";
}
