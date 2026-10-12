// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Models.Kubernetes;

namespace Framlux.FleetManagement.Services.Core.Kubernetes;

/// <summary>
/// Maps Kubernetes clusters and nodes from the database to the DTOs the web API returns.
/// </summary>
internal static class KubernetesClusterMapper
{
    /// <summary>
    /// Returns the name to show for a cluster: its operator-chosen name when it has one, else the name
    /// derived from its nodes.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="nodes">The cluster's nodes.</param>
    public static string NameOf(KubernetesCluster cluster, IEnumerable<KubernetesNode> nodes)
    {
        if (string.IsNullOrWhiteSpace(cluster.DisplayName) == false)
        {
            return cluster.DisplayName;
        }

        return KubernetesClusterNames.Derive(
            cluster.Distro, cluster.CaFingerprint, nodes.Select(n => (n.NodeName, n.LocalControlPlane)));
    }

    /// <summary>
    /// Maps a node, with the name of the machine it is linked to.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <param name="machineName">The linked machine's name, or null when the node is not linked.</param>
    public static KubernetesNodeDto ToNodeDto(KubernetesNode node, string? machineName)
    {
        return new KubernetesNodeDto
        {
            Id = node.Id,
            NodeName = node.NodeName,
            ControlPlane = node.LocalControlPlane,
            KubeletVersion = node.KubeletVersion,
            MachineId = node.MachineId,
            MachineName = machineName,
            FingerprintMismatch = node.FingerprintMismatch,
            LastLocalReportAt = node.LastLocalReportAt,
        };
    }

    /// <summary>
    /// Maps a cluster and its nodes to the list view.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="nodes">The cluster's nodes.</param>
    public static KubernetesClusterSummaryDto ToSummary(KubernetesCluster cluster, IReadOnlyCollection<KubernetesNodeWithMachine> nodes)
    {
        KubernetesClusterSummaryDto summary = new();
        FillSummary(summary, cluster, nodes);

        return summary;
    }

    /// <summary>
    /// Maps a cluster and its nodes to the detail view, with the nodes ordered by name.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="nodes">The cluster's nodes.</param>
    public static KubernetesClusterDetailDto ToDetail(KubernetesCluster cluster, IReadOnlyCollection<KubernetesNodeWithMachine> nodes)
    {
        KubernetesClusterDetailDto detail = new()
        {
            CaFingerprint = cluster.CaFingerprint,
            HasFingerprintMismatch = nodes.Any(n => n.Node.FingerprintMismatch),
            Nodes = nodes
                .OrderBy(n => n.Node.NodeName, StringComparer.Ordinal)
                .Select(n => ToNodeDto(n.Node, n.MachineName))
                .ToList(),
        };
        FillSummary(detail, cluster, nodes);

        return detail;
    }

    private static void FillSummary(KubernetesClusterSummaryDto target, KubernetesCluster cluster, IReadOnlyCollection<KubernetesNodeWithMachine> nodes)
    {
        int managed = nodes.Count(n => n.Node.MachineId != null);

        target.Id = cluster.Id;
        target.Name = NameOf(cluster, nodes.Select(n => n.Node));
        target.DisplayName = cluster.DisplayName;
        target.Distro = cluster.Distro;
        target.NodeCount = nodes.Count;
        target.ManagedNodeCount = managed;
        target.UnmanagedNodeCount = nodes.Count - managed;
        target.KubeletVersions = nodes
            .Select(n => n.Node.KubeletVersion)
            .Where(v => string.IsNullOrEmpty(v) == false)
            .Select(v => v!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        target.ReportingState = KubernetesClusterSummaryDto.IdentityOnlyReportingState;
        target.FirstSeenAt = cluster.FirstSeenAt;
        target.LastSeenAt = cluster.LastSeenAt;
    }
}
