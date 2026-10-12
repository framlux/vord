// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Models.Telemetry;

/// <summary>
/// KubernetesCluster telemetry payload (type=15), the snapshot of a cluster that only the holder of the cluster's
/// reporter Lease sends. A snapshot that carries a <see cref="ReporterError"/> lists no nodes.
/// </summary>
public sealed class KubernetesClusterPayload
{
    /// <summary>The uid of the cluster's kube-system namespace, which identifies the cluster. Empty on an error snapshot.</summary>
    public string KubeSystemUid { get; set; } = "";

    /// <summary>The Kubernetes version the API server reports.</summary>
    public string ClusterVersion { get; set; } = "";

    /// <summary>The role version recorded on the reporter Lease, 0 when it records none.</summary>
    public int RoleVersion { get; set; }

    /// <summary>Whether the cluster's role is older than the one the agent carries.</summary>
    public bool RoleOutdated { get; set; }

    /// <summary>The lowercase hex SHA-256 fingerprint of the cluster CA certificate as the sending host sees it, empty when it cannot tell.</summary>
    public string CaFingerprint { get; set; } = "";

    /// <summary>The Lease's transition count when the snapshot was taken, which fences a snapshot from an earlier holder.</summary>
    public long LeaseTransitions { get; set; }

    /// <summary>
    /// Why the snapshot has no nodes: <c>credentials_rejected</c>, <c>tls_ca_changed</c>, <c>api_unreachable</c> or
    /// <c>namespace_terminating</c>. Empty or null on a snapshot of the cluster.
    /// </summary>
    public string? ReporterError { get; set; }

    /// <summary>Whether nodes were left out to keep the snapshot within its size cap.</summary>
    public bool Truncated { get; set; }

    /// <summary>The nodes, sorted by name.</summary>
    public List<KubernetesClusterNodeDto> Nodes { get; set; } = [];

    /// <summary>The uid of the Lease the snapshot was taken under, which tells one Lease from the next one created after reporting was re-enabled.</summary>
    public string LeaseUid { get; set; } = "";

    /// <summary>When the Lease was created (ISO 8601), which orders the Leases of one cluster.</summary>
    public string LeaseCreatedAt { get; set; } = "";
}
