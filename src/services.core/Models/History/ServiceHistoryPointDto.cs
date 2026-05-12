// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.History;

/// <summary>
/// A single data point for service status history.
/// </summary>
public sealed class ServiceHistoryPointDto
{
    /// <summary>The timestamp of this data point.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Number of failed services at this point.</summary>
    public required int FailedCount { get; init; }

    /// <summary>Total number of services at this point.</summary>
    public required int TotalCount { get; init; }
}
