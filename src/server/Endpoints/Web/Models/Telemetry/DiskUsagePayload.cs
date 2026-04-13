// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// Disk usage telemetry payload (type=8) — array of mounts.
/// </summary>
public sealed class DiskUsagePayload
{
    /// <summary>List of disk usage entries per mount.</summary>
    public List<DiskUsageEntryDto> Disks { get; set; } = [];
}
