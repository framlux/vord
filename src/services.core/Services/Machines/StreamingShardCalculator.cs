// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Globalization;
using Framlux.FleetManagement.Services.Core.Infrastructure;

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Pure shard-assignment math for the telemetry-state projection. Machines are partitioned across
/// shards by <c>machineId % shardCount</c>; each shard takes its own advisory lock and tracks its
/// own high-water mark so the projection scales across multiple worker replicas instead of one.
/// </summary>
internal static class StreamingShardCalculator
{
    /// <summary>The advisory-lock name for the given shard index.</summary>
    internal static string LockNameForShard(int shardIndex)
    {
        return LockNames.StateStreamingShardPrefix + shardIndex.ToString(CultureInfo.InvariantCulture);
    }
}
