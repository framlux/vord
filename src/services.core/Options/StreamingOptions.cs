// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Options;

/// <summary>
/// Configuration for the telemetry-state projection worker. Sharding spreads projection across
/// multiple worker replicas; each replica runs one hosted service per shard index it is assigned.
/// </summary>
public sealed class StreamingOptions
{
    /// <summary>Total number of projection shards across the cluster. Defaults to 1 (no sharding).</summary>
    public int ShardCount { get; set; } = 1;

    /// <summary>Number of telemetry rows fetched per poll cycle. Defaults to 1000.</summary>
    public int BatchSize { get; set; } = 1000;
}
