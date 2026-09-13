// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// Service for tracking the capabilities an agent reports about itself, held in Redis.
/// </summary>
/// <remarks>
/// Liveness is not tracked here. It is decided by the health sweep from the timestamps on the
/// machine's summary row, so that one answer serves every surface rather than a Redis key
/// answering some and the swept column answering others.
/// </remarks>
public interface IMachinePingService
{
    /// <summary>
    /// Stores the agent's capability bitmask reported during configuration fetch.
    /// </summary>
    /// <param name="machineId">The machine identifier.</param>
    /// <param name="capabilities">The bitwise capabilities value.</param>
    Task SetAgentCapabilitiesAsync(long machineId, ulong capabilities);

    /// <summary>
    /// Gets the agent's capability bitmask for the specified machine.
    /// Returns 0 if no capabilities have been reported.
    /// </summary>
    /// <param name="machineId">The machine identifier.</param>
    /// <returns>The bitwise capabilities value.</returns>
    Task<ulong> GetAgentCapabilitiesAsync(long machineId);

    /// <summary>
    /// Gets agent capability bitmasks for multiple machines in a single batch operation.
    /// </summary>
    /// <param name="machineIds">The machine identifiers to query.</param>
    /// <returns>A dictionary mapping each machine ID to its capabilities value.</returns>
    Task<Dictionary<long, ulong>> GetAgentCapabilitiesBatchAsync(IEnumerable<long> machineIds);
}
