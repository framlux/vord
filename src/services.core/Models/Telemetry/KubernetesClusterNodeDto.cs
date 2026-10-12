// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Telemetry;

/// <summary>
/// One node of a cluster as the cluster itself reports it in a cluster snapshot.
/// </summary>
public sealed class KubernetesClusterNodeDto
{
    /// <summary>The node's name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The node's roles, the sorted suffixes of its node-role labels.</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>The status of the node's Ready condition: True, False or Unknown.</summary>
    public string ReadyStatus { get; set; } = "";

    /// <summary>When the Ready condition last changed status (ISO 8601), empty when the node reports none.</summary>
    public string ReadySince { get; set; } = "";

    /// <summary>The pressure and network conditions of the node whose status is not False.</summary>
    public List<KubernetesNodeConditionDto> Conditions { get; set; } = [];

    /// <summary>Whether the node is cordoned.</summary>
    public bool Unschedulable { get; set; }

    /// <summary>The machine id the node's kubelet reports, empty when it reports none.</summary>
    public string MachineId { get; set; } = "";

    /// <summary>The kubelet version the node reports.</summary>
    public string KubeletVersion { get; set; } = "";

    /// <summary>The container runtime and its version, as the node reports them.</summary>
    public string ContainerRuntimeVersion { get; set; } = "";

    /// <summary>How many pods that have not finished run on the node.</summary>
    public int PodCount { get; set; }

    /// <summary>How many pods the node can run, 0 when it does not say.</summary>
    public int PodCapacity { get; set; }
}
