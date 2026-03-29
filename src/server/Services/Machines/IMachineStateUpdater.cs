// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Server.Services.Machines;

/// <summary>
/// Event-driven updater that performs per-type partial UPSERTs on MachineState.
/// </summary>
public interface IMachineStateUpdater
{
    /// <summary>
    /// Updates the MachineState row for the given machine with the specified telemetry type and JSON payload.
    /// Only touches the columns relevant to that telemetry type.
    /// </summary>
    Task UpdateAsync(long machineId, short telemetryType, string payload, DateTimeOffset receivedAt, CancellationToken ct);
}
