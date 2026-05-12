// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.History;

/// <summary>
/// A single disk series in a multi-series disk history response.
/// </summary>
public sealed class DiskSeriesDto
{
    /// <summary>The device path (e.g., /dev/sda1).</summary>
    public required string Device { get; init; }

    /// <summary>The mount point (e.g., /).</summary>
    public required string MountPoint { get; init; }

    /// <summary>Time-bucketed or raw data points for this device.</summary>
    public required List<HistoryPointDto> Points { get; init; }

    /// <summary>Statistics for this device's usage.</summary>
    public required HistoryStatsDto Stats { get; init; }
}
