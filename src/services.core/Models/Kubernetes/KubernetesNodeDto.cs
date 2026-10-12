// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Services.Core.Models.Telemetry;

namespace Framlux.FleetManagement.Services.Core.Models.Kubernetes;

/// <summary>
/// A node of a Kubernetes cluster and the machine it is linked to, when there is one.
/// </summary>
public sealed class KubernetesNodeDto
{
    /// <summary>Node database ID.</summary>
    public long Id { get; set; }

    /// <summary>The Kubernetes node name.</summary>
    public string NodeName { get; set; } = "";

    /// <summary>True when the node's machine reported that it runs a control plane.</summary>
    public bool ControlPlane { get; set; }

    /// <summary>The kubelet version the node's machine reported, if any.</summary>
    public string? KubeletVersion { get; set; }

    /// <summary>The linked machine's ID, or null while the node is not managed by Vord.</summary>
    public long? MachineId { get; set; }

    /// <summary>The linked machine's name, or null while the node is not managed by Vord.</summary>
    public string? MachineName { get; set; }

    /// <summary>True while the node reports a CA fingerprint different from its cluster's.</summary>
    public bool FingerprintMismatch { get; set; }

    /// <summary>When the node's own machine last reported it, or null if only another source has.</summary>
    public DateTimeOffset? LastLocalReportAt { get; set; }

    /// <summary>The roles the cluster reporter lists for the node, such as <c>control-plane</c> or <c>etcd</c>; empty when it has not listed the node.</summary>
    public List<string> Roles { get; set; } = [];

    /// <summary>The status of the node's <c>Ready</c> condition (<c>True</c>, <c>False</c> or <c>Unknown</c>), or null when no snapshot has listed the node.</summary>
    public string? ReadyStatus { get; set; }

    /// <summary>When the node's <c>Ready</c> condition last changed.</summary>
    public DateTimeOffset? ReadySince { get; set; }

    /// <summary>The node's pressure and network conditions whose status is not <c>False</c>.</summary>
    public List<KubernetesNodeConditionDto> Conditions { get; set; } = [];

    /// <summary>True while the node is cordoned and takes no new pods.</summary>
    public bool Unschedulable { get; set; }

    /// <summary>When the node was cordoned, or null when it is schedulable or the time is unknown.</summary>
    public DateTimeOffset? UnschedulableSince { get; set; }

    /// <summary>The container runtime and version the node runs, such as <c>containerd://1.7.23</c>.</summary>
    public string? ContainerRuntimeVersion { get; set; }

    /// <summary>The number of pods scheduled on the node, or null when the snapshot did not count them.</summary>
    public int? PodCount { get; set; }

    /// <summary>The number of pods the node can run.</summary>
    public int? PodCapacity { get; set; }

    /// <summary>When a cluster snapshot last listed the node, or null if only local reports have.</summary>
    public DateTimeOffset? LastReporterSeenAt { get; set; }

    /// <summary>
    /// True when the node's kubelet version and the cluster's version both read as versions, and the kubelet
    /// is more than three minor versions behind the cluster or newer than it.
    /// </summary>
    public bool KubeletSkew { get; set; }
}
