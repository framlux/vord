// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.History;

/// <summary>
/// Summary statistics computed across all raw values in a series.
/// </summary>
public sealed class AggregationStats
{
    /// <summary>Minimum value in the series.</summary>
    public required double Min { get; init; }

    /// <summary>Average value across the series.</summary>
    public required double Avg { get; init; }

    /// <summary>Maximum value in the series.</summary>
    public required double Max { get; init; }

    /// <summary>95th percentile value.</summary>
    public required double P95 { get; init; }
}
