// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Service for tracking machine ping timestamps using Redis sorted sets.
/// </summary>
public interface IMachinePingService
{
    /// <summary>
    /// Records a ping for the specified machine at the current time.
    /// </summary>
    /// <param name="machineId">The machine identifier.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task RecordPingAsync(long machineId);

    /// <summary>
    /// Gets the most recent ping time for the specified machine.
    /// </summary>
    /// <param name="machineId">The machine identifier.</param>
    /// <returns>The last ping time, or null if no pings exist.</returns>
    Task<DateTimeOffset?> GetLastPingAsync(long machineId);

    /// <summary>
    /// Gets all ping timestamps within the specified time window.
    /// </summary>
    /// <param name="machineId">The machine identifier.</param>
    /// <param name="window">The time window to query from now.</param>
    /// <returns>An enumerable of ping timestamps within the window.</returns>
    Task<IEnumerable<DateTimeOffset>> GetPingHistoryAsync(long machineId, TimeSpan window);

    /// <summary>
    /// Checks whether the machine has pinged within the specified threshold.
    /// </summary>
    /// <param name="machineId">The machine identifier.</param>
    /// <param name="threshold">The maximum age of the last ping to consider online.</param>
    /// <returns>True if the machine is considered online.</returns>
    Task<bool> IsOnlineAsync(long machineId, TimeSpan threshold);

    /// <summary>
    /// Checks online status for multiple machines in a single batch operation.
    /// </summary>
    /// <param name="machineIds">The machine identifiers to check.</param>
    /// <param name="threshold">The maximum age of the last ping to consider online.</param>
    /// <returns>A dictionary mapping each machine ID to its online status.</returns>
    Task<Dictionary<long, bool>> AreOnlineAsync(IEnumerable<long> machineIds, TimeSpan threshold);

    /// <summary>
    /// Gets the most recent ping time for multiple machines in a single batch operation.
    /// </summary>
    /// <param name="machineIds">The machine identifiers to query.</param>
    /// <returns>A dictionary mapping each machine ID to its last ping time.</returns>
    Task<Dictionary<long, DateTimeOffset?>> GetLastPingsAsync(IEnumerable<long> machineIds);

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
