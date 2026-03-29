// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Text.Json.Serialization;

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// Package updates telemetry payload (type=11).
/// </summary>
public sealed class PackageUpdatesPayload
{
    /// <summary>Package manager name.</summary>
    [JsonPropertyName("package_manager")]
    public string PackageManager { get; set; } = "";

    /// <summary>List of available updates.</summary>
    [JsonPropertyName("updates")]
    public List<PackageUpdateDto> Updates { get; set; } = [];
}
