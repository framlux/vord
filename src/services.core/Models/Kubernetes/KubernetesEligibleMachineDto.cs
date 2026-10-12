// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Kubernetes;

/// <summary>
/// A machine that can be enrolled as a reporter for a cluster, with what the web needs to decide whether
/// a command can be sent to it.
/// </summary>
public sealed class KubernetesEligibleMachineDto
{
    /// <summary>The machine's ID.</summary>
    public long MachineId { get; set; }

    /// <summary>The machine's name.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>
    /// Whether the server has heard from the machine lately, by the same rule as the machine list. A paused
    /// host still heartbeats, so it is online.
    /// </summary>
    public bool Online { get; set; }

    /// <summary>True while the machine is paused for exceeding the tenant's machine limit, which makes the server refuse its commands with a conflict.</summary>
    public bool IsPaused { get; set; }

    /// <summary>True when the agent has reported the remote commands capability, without which the server refuses its commands.</summary>
    public bool RemoteCommandsEnabled { get; set; }

    /// <summary>True when the machine's latest report says it is already enrolled as a reporter.</summary>
    public bool Enrolled { get; set; }

    /// <summary>The IDs of the signing keys authorized to send commands to the machine, in ascending order.</summary>
    public List<int> AuthorizedSigningKeyIds { get; set; } = [];
}
