// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Server.Services.Telemetry;
using System.Collections.Concurrent;

namespace Framlux.FleetManagement.FunctionalTest.Infrastructure;

/// <summary>
/// In-memory implementation of <see cref="ITelemetryDeduplicationService"/> for functional testing
/// without Redis.
/// </summary>
public sealed class InMemoryTelemetryDeduplicationService : ITelemetryDeduplicationService
{
    private readonly ConcurrentDictionary<string, byte> _seen = new();

    /// <inheritdoc/>
    public Task<bool> TryMarkSeenAsync(string eventId)
    {
        bool isNew = _seen.TryAdd(eventId, 0);

        return Task.FromResult(isNew);
    }

    /// <inheritdoc/>
    public Task<Dictionary<string, bool>> TryMarkSeenBatchAsync(IEnumerable<string> eventIds)
    {
        Dictionary<string, bool> results = new();
        foreach (string eventId in eventIds)
        {
            results[eventId] = _seen.TryAdd(eventId, 0);
        }

        return Task.FromResult(results);
    }
}
