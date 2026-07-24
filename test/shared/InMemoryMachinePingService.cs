// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Machines;
using System.Collections.Concurrent;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// In-memory implementation of <see cref="IMachinePingService"/> for testing without Redis.
/// Mirrors the production service by retaining only each machine's most recent ping.
/// </summary>
public sealed class InMemoryMachinePingService : IMachinePingService
{
    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastPings = new();
    private readonly ConcurrentDictionary<long, ulong> _capabilities = new();

    /// <inheritdoc/>
    public Task RecordPingAsync(long machineId)
    {
        _lastPings[machineId] = DateTimeOffset.UtcNow;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<DateTimeOffset?> GetLastPingAsync(long machineId)
    {
        if (_lastPings.TryGetValue(machineId, out DateTimeOffset lastPing))
        {
            return Task.FromResult<DateTimeOffset?>(lastPing);
        }

        return Task.FromResult<DateTimeOffset?>(null);
    }

    /// <inheritdoc/>
    public Task<bool> IsOnlineAsync(long machineId, TimeSpan threshold)
    {
        if (_lastPings.TryGetValue(machineId, out DateTimeOffset lastPing))
        {
            return Task.FromResult(DateTimeOffset.UtcNow - lastPing <= threshold);
        }

        return Task.FromResult(false);
    }

    /// <inheritdoc/>
    public Task<Dictionary<long, bool>> AreOnlineAsync(IEnumerable<long> machineIds, TimeSpan threshold)
    {
        Dictionary<long, bool> result = new();
        foreach (long machineId in machineIds)
        {
            bool online = false;
            if (_lastPings.TryGetValue(machineId, out DateTimeOffset lastPing))
            {
                online = DateTimeOffset.UtcNow - lastPing <= threshold;
            }
            result[machineId] = online;
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<Dictionary<long, DateTimeOffset?>> GetLastPingsAsync(IEnumerable<long> machineIds)
    {
        Dictionary<long, DateTimeOffset?> result = new();
        foreach (long machineId in machineIds)
        {
            DateTimeOffset? lastPing = _lastPings.TryGetValue(machineId, out DateTimeOffset ping) ? ping : null;
            result[machineId] = lastPing;
        }

        return Task.FromResult(result);
    }

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
