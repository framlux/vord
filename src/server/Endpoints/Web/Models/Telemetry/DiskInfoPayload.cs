// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// Disk info telemetry payload (type=5).
/// </summary>
public sealed class DiskInfoPayload
{
    /// <summary>List of disk info entries per mount.</summary>
    public List<DiskInfoEntryDto> Disks { get; set; } = [];
}
