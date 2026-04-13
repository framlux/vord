// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// SSH session telemetry payload (type=9).
/// </summary>
public sealed class SshSessionPayload
{
    /// <summary>Username.</summary>
    public string User { get; set; } = "";

    /// <summary>Source IP address.</summary>
    public string SourceIp { get; set; } = "";

    /// <summary>Source port.</summary>
    public int SourcePort { get; set; }

    /// <summary>Action (connect/disconnect/failed).</summary>
    public string Action { get; set; } = "";

    /// <summary>Authentication method.</summary>
    public string AuthMethod { get; set; } = "";

    /// <summary>Event timestamp.</summary>
    public string Timestamp { get; set; } = "";
}
