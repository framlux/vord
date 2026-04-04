// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Updates MachineState rows from telemetry data. Supports both single-item updates
/// (legacy/fallback) and batched updates for the queue-based consumer.
/// </summary>
public interface IMachineStateUpdater
{
    /// <summary>
    /// Updates the MachineState row for the given machine with the specified telemetry type and JSON payload.
    /// Only touches the columns relevant to that telemetry type.
    /// </summary>
    Task UpdateAsync(long machineId, short telemetryType, string payload, DateTimeOffset receivedAt, CancellationToken ct);

    /// <summary>
    /// Applies coalesced state updates for multiple machines. Groups updates by telemetry type
    /// and issues batch UPDATE statements to minimize database round-trips.
    /// Called by the <see cref="MachineStateConsumerService"/> after reading from Redis Streams.
    /// </summary>
    /// <param name="updatesByMachine">Updates grouped by machine ID.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UpdateBatchAsync(Dictionary<long, List<StateUpdateMessage>> updatesByMachine, CancellationToken ct);
}
