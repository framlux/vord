// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Machines;
using System.Collections.Concurrent;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// In-memory implementation of <see cref="IMachinePingService"/> for testing without Redis.
/// Mirrors the production service by retaining only each machine's most recently reported
/// capabilities.
/// </summary>
public sealed class InMemoryMachinePingService : IMachinePingService
{
    private readonly ConcurrentDictionary<long, ulong> _capabilities = new();

    /// <summary>
    /// When set, every capability read throws it, as a Redis outage would, so a test can prove what a
    /// surface does when the capabilities cannot be read. Writes are unaffected.
    /// </summary>
    public Exception? ReadFailure { get; set; }

    /// <inheritdoc/>
    public Task SetAgentCapabilitiesAsync(long machineId, ulong capabilities)
    {
        _capabilities[machineId] = capabilities;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<ulong> GetAgentCapabilitiesAsync(long machineId)
    {
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        _capabilities.TryGetValue(machineId, out ulong capabilities);

        return Task.FromResult(capabilities);
    }

    /// <inheritdoc/>
    public Task<Dictionary<long, ulong>> GetAgentCapabilitiesBatchAsync(IEnumerable<long> machineIds)
    {
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        Dictionary<long, ulong> result = new();
        foreach (long machineId in machineIds)
        {
            _capabilities.TryGetValue(machineId, out ulong caps);
            result[machineId] = caps;
        }

        return Task.FromResult(result);
    }
}
