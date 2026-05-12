// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Telemetry;

/// <summary>
/// Memory info telemetry payload (type=4).
/// </summary>
public sealed class MemoryInfoPayload
{
    /// <summary>Total memory in bytes.</summary>
    public long MemoryTotal { get; set; }

    /// <summary>Free memory in bytes.</summary>
    public long MemoryFree { get; set; }

    /// <summary>Available memory in bytes.</summary>
    public long MemoryAvailable { get; set; }

    /// <summary>Total swap in bytes.</summary>
    public long SwapTotal { get; set; }

    /// <summary>Free swap in bytes.</summary>
    public long SwapFree { get; set; }
}
