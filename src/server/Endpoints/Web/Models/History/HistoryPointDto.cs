// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Web.Models.History;

/// <summary>
/// A single data point in a time-series history response.
/// </summary>
public sealed class HistoryPointDto
{
    /// <summary>The timestamp of this data point.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The metric value (percentage for CPU/Memory/Disk).</summary>
    public required double Value { get; init; }
}
