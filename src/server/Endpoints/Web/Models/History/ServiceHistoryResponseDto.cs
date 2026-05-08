// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.History;

/// <summary>
/// History response for service status showing failed/total over time.
/// </summary>
public sealed class ServiceHistoryResponseDto
{
    /// <summary>Time-bucketed or raw data points.</summary>
    public required List<ServiceHistoryPointDto> Points { get; init; }

    /// <summary>Statistics for failed service count.</summary>
    public required HistoryStatsDto Stats { get; init; }

    /// <summary>The bucket size in seconds. Zero when raw data was returned.</summary>
    public required int BucketSeconds { get; init; }

    /// <summary>The total number of raw data points before aggregation.</summary>
    public required int RawPointCount { get; init; }
}
