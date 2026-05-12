// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Telemetry;

/// <summary>
/// Memory utilization telemetry payload (type=7).
/// </summary>
public sealed class MemoryUsagePayload
{
    /// <summary>Total memory in bytes.</summary>
    public long MemoryTotal { get; set; }

    /// <summary>Used memory in bytes.</summary>
    public long MemoryUsed { get; set; }

    /// <summary>Memory usage percentage.</summary>
    public int MemoryUsagePercent { get; set; }
}
