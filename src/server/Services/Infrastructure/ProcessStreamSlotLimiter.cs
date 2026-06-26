// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using System.Threading;

namespace Framlux.FleetManagement.Server.Services.Infrastructure;

/// <summary>
/// A conservative per-process cap on concurrent telemetry streams, used as a fail-closed fallback
/// when the Redis-backed per-machine cap is unavailable. Bounds total streams on a single replica
/// so a Redis outage degrades to a local limit rather than unbounded acceptance.
/// </summary>
public sealed class ProcessStreamSlotLimiter
{
    private readonly int _max;
    private int _current;

    /// <summary>Creates the limiter with the given per-process ceiling.</summary>
    /// <param name="maxPerProcess">Maximum concurrent streams allowed on this replica.</param>
    public ProcessStreamSlotLimiter(int maxPerProcess)
    {
        _max = maxPerProcess;
    }

    /// <summary>Tries to take a slot. Returns false when the per-process cap is reached.</summary>
    public bool TryAcquire()
    {
        int updated = Interlocked.Increment(ref _current);
        if (updated > _max)
        {
            Interlocked.Decrement(ref _current);

            return false;
        }

        return true;
    }

    /// <summary>Releases a previously-taken slot, never letting the count go below zero.</summary>
    public void Release()
    {
        int updated = Interlocked.Decrement(ref _current);
        if (updated < 0)
        {
            Interlocked.Increment(ref _current);
        }
    }
}
