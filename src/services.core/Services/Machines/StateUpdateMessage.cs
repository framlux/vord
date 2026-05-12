// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Machines;

/// <summary>
/// A single telemetry state update to be applied to MachineState.
/// </summary>
public sealed class StateUpdateMessage
{
    /// <summary>
    /// The telemetry type identifier.
    /// </summary>
    public short TelemetryType { get; init; }

    /// <summary>
    /// The JSON payload for this telemetry type.
    /// </summary>
    public required string Payload { get; init; }

    /// <summary>
    /// When the telemetry was received by the server.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; init; }
}
