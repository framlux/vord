// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Telemetry;

/// <summary>
/// CPU utilization telemetry payload (type=6). The eight jiffy buckets are a decomposition of
/// <see cref="CpuUsagePercent"/>, which the agent derives as 100 minus idle: iowait and steal are
/// already inside the headline figure rather than additional load. Each bucket is truncated to a
/// whole percent by the agent, so sustained sub-1% values arrive as 0.
/// </summary>
public sealed class CpuUsagePayload
{
    /// <summary>CPU usage percentage.</summary>
    public int CpuUsagePercent { get; set; }

    /// <summary>Percentage of ticks spent in user mode.</summary>
    public int UserTime { get; set; }

    /// <summary>Percentage of ticks spent in kernel mode.</summary>
    public int SystemTime { get; set; }

    /// <summary>Percentage of ticks spent on niced user processes. Rendered in the tooltip only.</summary>
    public int NiceTime { get; set; }

    /// <summary>Percentage of idle ticks. Rendered in the tooltip only.</summary>
    public int IdleTime { get; set; }

    /// <summary>Percentage of ticks waiting on I/O.</summary>
    public int IowaitTime { get; set; }

    /// <summary>Percentage of ticks servicing hardware interrupts. Rendered in the tooltip only.</summary>
    public int IrqTime { get; set; }

    /// <summary>Percentage of ticks servicing soft interrupts. Rendered in the tooltip only.</summary>
    public int SoftirqTime { get; set; }

    /// <summary>Percentage of ticks stolen by the hypervisor from this guest.</summary>
    public int StealTime { get; set; }
}
