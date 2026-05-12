// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Telemetry;

/// <summary>
/// Single disk mount usage entry.
/// </summary>
public sealed class DiskUsageEntryDto
{
    /// <summary>Device path.</summary>
    public string Device { get; set; } = "";

    /// <summary>Mount path.</summary>
    public string Path { get; set; } = "";

    /// <summary>Block size in bytes.</summary>
    public long BlocksSize { get; set; }

    /// <summary>Total blocks.</summary>
    public long Blocks { get; set; }

    /// <summary>Free blocks.</summary>
    public long BlocksFree { get; set; }

    /// <summary>Available blocks.</summary>
    public long BlocksAvailable { get; set; }

    /// <summary>Used blocks.</summary>
    public long BlocksUsed { get; set; }

    /// <summary>Usage percentage.</summary>
    public int UsagePercent { get; set; }
}
