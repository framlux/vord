// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.Telemetry;

/// <summary>
/// CPU utilization telemetry payload (type=6).
/// </summary>
public sealed class CpuUsagePayload
{
    /// <summary>CPU usage percentage.</summary>
    public int CpuUsagePercent { get; set; }
}
