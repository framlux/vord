// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json.Serialization;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// Single systemd service entry.
/// </summary>
public sealed class ServiceEntryDto
{
    /// <summary>Service unit name.</summary>
    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "";

    /// <summary>Load state.</summary>
    [JsonPropertyName("load_state")]
    public string LoadState { get; set; } = "";

    /// <summary>Active state.</summary>
    [JsonPropertyName("active_state")]
    public string ActiveState { get; set; } = "";

    /// <summary>Sub-state.</summary>
    [JsonPropertyName("sub_state")]
    public string SubState { get; set; } = "";

    /// <summary>Service description.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}
