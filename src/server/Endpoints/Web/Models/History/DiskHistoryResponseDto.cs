// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.History;

/// <summary>
/// History response for disk utilization with multiple series (one per mount point).
/// </summary>
public sealed class DiskHistoryResponseDto
{
    /// <summary>One series per disk device/mount point.</summary>
    public required List<DiskSeriesDto> Series { get; init; }

    /// <summary>The bucket size in seconds. Zero when raw data was returned.</summary>
    public required int BucketSeconds { get; init; }

    /// <summary>The total number of raw data points before aggregation.</summary>
    public required int RawPointCount { get; init; }
}
