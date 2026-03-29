// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json.Serialization;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// Power supply reading.
/// </summary>
public sealed class PowerSupplyReadingDto
{
    /// <summary>PSU name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Power draw in watts.</summary>
    [JsonPropertyName("watts")]
    public int Watts { get; set; }

    /// <summary>PSU status.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}
