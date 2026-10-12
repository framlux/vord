// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Kubernetes;

/// <summary>
/// A Kubernetes cluster as listed for a tenant: its name, size and when it was last seen.
/// </summary>
public class KubernetesClusterSummaryDto
{
    /// <summary>The reporting state of every cluster while clusters are built only from machines' local identity reports.</summary>
    public const string IdentityOnlyReportingState = "identity_only";

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

    /// <summary>How much of the cluster Vord can see. Always <see cref="IdentityOnlyReportingState"/> for now.</summary>
    public string ReportingState { get; set; } = IdentityOnlyReportingState;

    /// <summary>When the cluster was first created from a node report.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>When a node report last confirmed the cluster.</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}
