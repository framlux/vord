// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Machines;

/// <summary>
/// Machine online/offline status for real-time polling.
/// </summary>
public sealed class MachineStatusDto
{
    /// <summary>
    /// Whether the machine is currently online.
    /// </summary>
    public bool IsOnline { get; set; }

    /// <summary>
    /// The last time the machine sent a ping.
    /// </summary>
    public DateTimeOffset? LastPing { get; set; }

    /// <summary>
    /// Whether the agent on this machine accepts remote commands.
    /// </summary>
    public bool CommandsEnabled { get; set; }

    /// <summary>
    /// The computed health status combining online state and telemetry.
    /// </summary>
    public MachineHealthStatus HealthStatus { get; set; }
}
