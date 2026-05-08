// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.History;

/// <summary>
/// A single data point in an aggregated time series.
/// </summary>
public sealed class AggregatedPoint
{
    /// <summary>The timestamp for this point (bucket start or raw timestamp).</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The value (average if bucketed, raw if unbucketed).</summary>
    public required double Value { get; init; }
}
