// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Telemetry;

/// <summary>
/// Service status telemetry payload (type=12).
/// </summary>
public sealed class ServiceStatusPayload
{
    /// <summary>List of systemd services.</summary>
    public List<ServiceEntryDto> Services { get; set; } = [];
}
