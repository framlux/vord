// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Models;

/// <summary>One node of a cluster as the holder of the reporter Lease reported it.</summary>
/// <param name="Name">The Kubernetes node name.</param>
/// <param name="MachineId">The <c>/etc/machine-id</c> the node's kubelet reports, or null when it reports none.</param>
/// <param name="Roles">The node's roles, the sorted suffixes of its node-role labels.</param>
/// <param name="ReadyStatus">The status of the node's <c>Ready</c> condition, or null when it reports none.</param>
/// <param name="ReadySince">When the <c>Ready</c> condition last changed status, or null when the cluster did not say.</param>
/// <param name="Conditions">The pressure and network conditions of the node whose status is not <c>False</c>.</param>
/// <param name="Unschedulable">True while the node is cordoned.</param>
/// <param name="KubeletVersion">The kubelet version the node reports, or null when it reports none.</param>
/// <param name="ContainerRuntimeVersion">The container runtime and its version, or null when the node reports none.</param>
/// <param name="PodCount">How many pods that have not finished run on the node.</param>
/// <param name="PodCapacity">How many pods the node can run, or null when it does not say.</param>
public sealed record ClusterSnapshotNode(
    string Name,
    string? MachineId,
    IReadOnlyList<string> Roles,
    string? ReadyStatus,
    DateTimeOffset? ReadySince,
    IReadOnlyList<ClusterSnapshotCondition> Conditions,
    bool Unschedulable,
    string? KubeletVersion,
    string? ContainerRuntimeVersion,
    int PodCount,
    int? PodCapacity);
