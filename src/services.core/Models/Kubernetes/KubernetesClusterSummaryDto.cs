// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Kubernetes;

/// <summary>
/// A Kubernetes cluster as listed for a tenant: its name, size and when it was last seen.
/// </summary>
public class KubernetesClusterSummaryDto
{
    /// <summary>Cluster database ID.</summary>
    public long Id { get; set; }

    /// <summary>The name to show: the operator-chosen name when there is one, else the derived default.</summary>
    public string Name { get; set; } = "";

    /// <summary>The operator-chosen name as stored, or null while the cluster uses its derived name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>The Kubernetes distribution: k3s, rke2, kubeadm, k0s, microk8s or unknown.</summary>
    public string Distro { get; set; } = "";

    /// <summary>The number of nodes known in the cluster.</summary>
    public int NodeCount { get; set; }

    /// <summary>The number of nodes linked to a machine Vord manages.</summary>
    public int ManagedNodeCount { get; set; }

    /// <summary>The number of nodes not linked to a managed machine.</summary>
    public int UnmanagedNodeCount { get; set; }

    /// <summary>The distinct kubelet versions across the nodes, ordinal-sorted, with empty values left out.</summary>
    public List<string> KubeletVersions { get; set; } = [];

    /// <summary>The number of nodes whose <c>Ready</c> condition is <c>True</c>.</summary>
    public int ReadyCount { get; set; }

    /// <summary>The number of nodes whose <c>Ready</c> condition is <c>False</c>.</summary>
    public int NotReadyCount { get; set; }

    /// <summary>The number of nodes with any other status, including those no cluster snapshot has described.</summary>
    public int UnknownCount { get; set; }

    /// <summary>
    /// How much of the cluster Vord can see through the cluster reporter: <c>off</c>, <c>error</c>,
    /// <c>role_outdated</c>, <c>stale</c> or <c>active</c>.
    /// </summary>
    public string ReportingState { get; set; } = "off";

    /// <summary>The ID of the machine that holds the cluster's reporter Lease and sent the latest snapshot, or null when none has.</summary>
    public long? ActiveReporterMachineId { get; set; }

    /// <summary>The name of the machine in <see cref="ActiveReporterMachineId"/>.</summary>
    public string? ActiveReporterMachineName { get; set; }

    /// <summary>The Kubernetes server version the latest snapshot reported, or null when there has been none.</summary>
    public string? ClusterVersion { get; set; }

    /// <summary>
    /// The error that puts the cluster in the <c>error</c> state (<c>credentials_rejected</c>,
    /// <c>tls_ca_changed</c>, <c>api_unreachable</c> or <c>namespace_terminating</c>), and null in every other state.
    /// </summary>
    public string? ReporterError { get; set; }

    /// <summary>True when the latest snapshot listed only some of the cluster's nodes to fit its size cap.</summary>
    public bool Truncated { get; set; }

    /// <summary>When the server received the latest snapshot, or null when it has received none.</summary>
    public DateTimeOffset? LastSnapshotReceivedAt { get; set; }

    /// <summary>
    /// When the cluster's node values date from, once they are stale: the time of the latest snapshot while
    /// reporters are enrolled and that snapshot is older than the staleness threshold, and null otherwise.
    /// </summary>
    public DateTimeOffset? StaleSince { get; set; }

    /// <summary>When the cluster was first created from a node report.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>When a node report last confirmed the cluster.</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}
