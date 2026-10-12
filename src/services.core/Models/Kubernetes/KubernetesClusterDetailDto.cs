// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Kubernetes;

/// <summary>
/// A Kubernetes cluster with its CA fingerprint and nodes, for the cluster page.
/// </summary>
public sealed class KubernetesClusterDetailDto : KubernetesClusterSummaryDto
{
    /// <summary>The lowercase hex SHA-256 fingerprint of the cluster CA certificate.</summary>
    public string CaFingerprint { get; set; } = "";

    /// <summary>
    /// True when any node reports a CA fingerprint different from the cluster's, as happens while a CA
    /// rotation is in progress.
    /// </summary>
    public bool HasFingerprintMismatch { get; set; }

    /// <summary>The UID of the cluster's <c>kube-system</c> namespace, or null while no snapshot has reported it.</summary>
    public string? KubeSystemUid { get; set; }

    /// <summary>The cluster's nodes, ordered by node name.</summary>
    public List<KubernetesNodeDto> Nodes { get; set; } = [];

    /// <summary>The machines enrolled as reporters for the cluster, ordered by machine name.</summary>
    public List<KubernetesCandidateDto> Candidates { get; set; } = [];

    /// <summary>The machines that could be enrolled as reporters for the cluster, ordered by machine name.</summary>
    public List<KubernetesEligibleMachineDto> EligibleMachines { get; set; } = [];
}
