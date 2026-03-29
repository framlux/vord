// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json.Serialization;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// SSH session telemetry payload (type=9).
/// </summary>
public sealed class SshSessionPayload
{
    /// <summary>Username.</summary>
    [JsonPropertyName("user")]
    public string User { get; set; } = "";

    /// <summary>Source IP address.</summary>
    [JsonPropertyName("source_ip")]
    public string SourceIp { get; set; } = "";

    /// <summary>Source port.</summary>
    [JsonPropertyName("source_port")]
    public int SourcePort { get; set; }

    /// <summary>Action (connect/disconnect/failed).</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    /// <summary>Authentication method.</summary>
    [JsonPropertyName("auth_method")]
    public string AuthMethod { get; set; } = "";

    /// <summary>Event timestamp.</summary>
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";
}
