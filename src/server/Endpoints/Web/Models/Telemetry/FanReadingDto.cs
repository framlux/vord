// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json.Serialization;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// Fan sensor reading.
/// </summary>
public sealed class FanReadingDto
{
    /// <summary>Fan name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Fan speed in RPM.</summary>
    [JsonPropertyName("rpm")]
    public int Rpm { get; set; }

    /// <summary>Sensor status.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";
}
