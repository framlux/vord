// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.History;

/// <summary>
/// Result of aggregating a single time-series metric.
/// </summary>
public sealed class AggregatedSeries
{
    /// <summary>The time-bucketed (or raw) data points.</summary>
    public required List<AggregatedPoint> Points { get; init; }

    /// <summary>Statistics computed across all raw values before bucketing.</summary>
    public required AggregationStats Stats { get; init; }

    /// <summary>The bucket size in seconds. Zero when raw data was returned unbucketed.</summary>
    public required int BucketSeconds { get; init; }

    /// <summary>The total number of raw data points before aggregation.</summary>
    public required int RawPointCount { get; init; }
}
