// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// Single package update entry.
/// </summary>
public sealed class PackageUpdateDto
{
    /// <summary>Package name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Currently installed version.</summary>
    public string CurrentVersion { get; set; } = "";

    /// <summary>Available version.</summary>
    public string AvailableVersion { get; set; } = "";

    /// <summary>Whether this is a security update.</summary>
    public bool IsSecurityUpdate { get; set; }
}
