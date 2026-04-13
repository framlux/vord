// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// Package updates telemetry payload (type=11).
/// </summary>
public sealed class PackageUpdatesPayload
{
    /// <summary>Package manager name.</summary>
    public string PackageManager { get; set; } = "";

    /// <summary>List of available updates.</summary>
    public List<PackageUpdateDto> Updates { get; set; } = [];
}
