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

    /// <inheritdoc/>
    public Task SetAgentCapabilitiesAsync(long machineId, ulong capabilities)
    {
        _capabilities[machineId] = capabilities;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<ulong> GetAgentCapabilitiesAsync(long machineId)
    {
        _capabilities.TryGetValue(machineId, out ulong capabilities);

        return Task.FromResult(capabilities);
    }

    /// <inheritdoc/>
    public Task<Dictionary<long, ulong>> GetAgentCapabilitiesBatchAsync(IEnumerable<long> machineIds)
    {
        Dictionary<long, ulong> result = new();
        foreach (long machineId in machineIds)
        {
            _capabilities.TryGetValue(machineId, out ulong caps);
            result[machineId] = caps;
        }

        return Task.FromResult(result);
    }
}
