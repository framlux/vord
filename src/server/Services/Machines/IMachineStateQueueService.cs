// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Publishes MachineState update messages to Redis Streams for asynchronous processing.
/// </summary>
public interface IMachineStateQueueService
{
    /// <summary>
    /// Publishes one or more state update messages for a machine to the partitioned Redis Stream.
    /// </summary>
    /// <param name="machineId">The machine to update.</param>
    /// <param name="items">The telemetry items to publish.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PublishAsync(long machineId, IReadOnlyList<StateUpdateMessage> items, CancellationToken ct);
}
