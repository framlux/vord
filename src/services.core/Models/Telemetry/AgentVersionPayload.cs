// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Telemetry;

/// <summary>
/// AgentVersion telemetry payload (type=13).
/// </summary>
public sealed class AgentVersionPayload
{
    /// <summary>The running agent's build version.</summary>
    public string Version { get; set; } = "";
}
