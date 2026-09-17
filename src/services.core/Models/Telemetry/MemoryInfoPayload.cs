// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Telemetry;

/// <summary>
/// Memory and swap totals telemetry payload (type=4). The agent enqueues this on the slow tick
/// (15 minutes by default), so these figures are materially older than the memory-usage payload
/// they are displayed beside.
/// </summary>
public sealed class MemoryInfoPayload
{
    /// <summary>Total physical memory in bytes.</summary>
    public long MemoryTotal { get; set; }

    /// <summary>Free physical memory in bytes.</summary>
    public long MemoryFree { get; set; }

    /// <summary>Memory available for new allocations without swapping, in bytes.</summary>
    public long MemoryAvailable { get; set; }

    /// <summary>Total swap space in bytes. Zero on a host with no swap configured.</summary>
    public long SwapTotal { get; set; }

    /// <summary>Unused swap space in bytes.</summary>
    public long SwapFree { get; set; }
}
