// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.History;

/// <summary>
/// A raw timestamped value extracted from a telemetry payload before aggregation.
/// </summary>
public sealed class TimestampedValue
{
    /// <summary>The timestamp of the telemetry reading.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The numeric metric value.</summary>
    public required double Value { get; init; }
}
