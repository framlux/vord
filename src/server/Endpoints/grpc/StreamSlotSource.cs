// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Endpoints.Grpc;

/// <summary>
/// Identifies which limiter granted a telemetry stream slot, so the matching counter is released
/// on stream close. A slot taken from the Redis-backed per-machine cap must be returned to Redis;
/// a slot taken from the per-process fallback during a Redis outage must be returned locally.
/// </summary>
internal enum StreamSlotSource
{
    /// <summary>The slot was granted by the Redis-backed per-machine cap.</summary>
    Redis,

    /// <summary>The slot was granted by the per-process fallback during a Redis outage.</summary>
    Process,
}
