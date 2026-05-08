// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.History;

/// <summary>
/// History response for single-metric time series (CPU, Memory, Services).
/// </summary>
public sealed class HistoryResponseDto
{
    /// <summary>Time-bucketed or raw data points.</summary>
    public required List<HistoryPointDto> Points { get; init; }

    /// <summary>Statistics computed across all raw values.</summary>
    public required HistoryStatsDto Stats { get; init; }

    /// <summary>The bucket size in seconds. Zero when raw data was returned.</summary>
    public required int BucketSeconds { get; init; }

    /// <summary>The total number of raw data points before aggregation.</summary>
    public required int RawPointCount { get; init; }
}
