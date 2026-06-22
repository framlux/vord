// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// SQL grouping projection mapping a machine health status value to the number of
/// machines in that state. Used by fleet health aggregation queries so the grouping
/// is computed in the database rather than in memory.
/// </summary>
public sealed class HealthStatusCount
{
    /// <summary>The health status value (0 healthy, 1 warning, 2 critical, 3 offline).</summary>
    public short HealthStatus { get; set; }

    /// <summary>The number of machines reporting this health status.</summary>
    public int Count { get; set; }
}
