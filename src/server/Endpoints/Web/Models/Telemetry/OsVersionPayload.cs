// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json.Serialization;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// OsVersion telemetry payload (type=2).
/// </summary>
public sealed class OsVersionPayload
{
    /// <summary>OS name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>OS version string.</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary>Platform identifier.</summary>
    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "";

    /// <summary>OS architecture.</summary>
    [JsonPropertyName("arch")]
    public string Arch { get; set; } = "";

    /// <summary>Kernel build string.</summary>
    [JsonPropertyName("build")]
    public string Build { get; set; } = "";
}
