// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Machines;
using System.Collections.Concurrent;

namespace Framlux.FleetManagement.Test.Infrastructure;

/// <summary>
/// In-memory implementation of <see cref="IMachinePingService"/> for testing without Redis.
/// </summary>
public sealed class InMemoryMachinePingService : IMachinePingService
{
    private readonly ConcurrentDictionary<long, List<DateTimeOffset>> _pings = new();
    private readonly ConcurrentDictionary<long, ulong> _capabilities = new();

    /// <inheritdoc/>
    public Task RecordPingAsync(long machineId)
    {
        _pings.AddOrUpdate(machineId,
            _ => [DateTimeOffset.UtcNow],
            (_, list) => { list.Add(DateTimeOffset.UtcNow); return list; });

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<DateTimeOffset?> GetLastPingAsync(long machineId)
    {
        if (_pings.TryGetValue(machineId, out List<DateTimeOffset>? pings) && pings.Count > 0)
        {
            return Task.FromResult<DateTimeOffset?>(pings[^1]);
        }

        return Task.FromResult<DateTimeOffset?>(null);
    }

    /// <inheritdoc/>
    public Task<IEnumerable<DateTimeOffset>> GetPingHistoryAsync(long machineId, TimeSpan window)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - window;
        if (_pings.TryGetValue(machineId, out List<DateTimeOffset>? pings))
        {
            return Task.FromResult<IEnumerable<DateTimeOffset>>(
                pings.Where(p => p >= cutoff).OrderDescending().ToList());
        }

        return Task.FromResult<IEnumerable<DateTimeOffset>>([]);
    }

    /// <inheritdoc/>
    public Task<bool> IsOnlineAsync(long machineId, TimeSpan threshold)
    {
        if (_pings.TryGetValue(machineId, out List<DateTimeOffset>? pings) && pings.Count > 0)
        {
            return Task.FromResult(DateTimeOffset.UtcNow - pings[^1] <= threshold);
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
            if (_pings.TryGetValue(machineId, out List<DateTimeOffset>? pings) && pings.Count > 0)
            {
                online = DateTimeOffset.UtcNow - pings[^1] <= threshold;
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
            DateTimeOffset? lastPing = null;
            if (_pings.TryGetValue(machineId, out List<DateTimeOffset>? pings) && pings.Count > 0)
            {
                lastPing = pings[^1];
            }
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
