// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Telemetry;

/// <summary>
/// OsVersion telemetry payload (type=2).
/// </summary>
public sealed class OsVersionPayload
{
    /// <summary>OS name.</summary>
    public string Name { get; set; } = "";

    /// <summary>OS version string.</summary>
    public string Version { get; set; } = "";

    /// <summary>Platform identifier.</summary>
    public string Platform { get; set; } = "";

    /// <summary>OS architecture.</summary>
    public string Arch { get; set; } = "";

    /// <summary>Kernel build string.</summary>
    public string Build { get; set; } = "";
}
