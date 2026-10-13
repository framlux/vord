// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Services.Core.Infrastructure;
using Framlux.FleetManagement.Services.Core.Models.Kubernetes;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;
using System.Text.Json;

namespace Framlux.FleetManagement.Services.Core.Kubernetes;

/// <summary>
/// Maps Kubernetes clusters and nodes from the database to the DTOs the web API returns.
/// </summary>
internal static class KubernetesClusterMapper
{
    private const string ReadyTrue = "True";
    private const string ReadyFalse = "False";

    /// <summary>
    /// Returns the name to show for a cluster: its operator-chosen name when it has one, else the name
    /// derived from its nodes.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="nodes">The cluster's nodes.</param>
    public static string NameOf(KubernetesCluster cluster, IEnumerable<KubernetesNode> nodes)
    {
        if (HasDisplayName(cluster))
        {
            return cluster.DisplayName!;
        }

        return KubernetesClusterNames.Derive(
            cluster.Distro, cluster.CaFingerprint, nodes.Select(n => (n.NodeName, n.LocalControlPlane, (IReadOnlyCollection<string>)RolesOf(n))));
    }

    /// <summary>
    /// Returns whether an operator has named the cluster, which makes its nodes irrelevant to its name.
    /// A blank name counts as none.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    public static bool HasDisplayName(KubernetesCluster cluster)
    {
        return string.IsNullOrWhiteSpace(cluster.DisplayName) == false;
    }

    /// <summary>
    /// Reads the roles the cluster reporter listed for a node. Text that is not a JSON array of strings
    /// reads as no roles, so a damaged value never turns a cluster page into an error.
    /// </summary>
    /// <param name="node">The node.</param>
    public static List<string> RolesOf(KubernetesNode node)
    {
        return ReadStoredList<string>(node.Roles);
    }

    /// <summary>
    /// Maps a node, with the name of the machine it is linked to.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <param name="machineName">The linked machine's name, or null when the node is not linked.</param>
    /// <param name="clusterVersion">The cluster's server version, which the node's kubelet version is compared with.</param>
    public static KubernetesNodeDto ToNodeDto(KubernetesNode node, string? machineName, string? clusterVersion)
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
            Roles = RolesOf(node),
            ReadyStatus = node.ReadyStatus,
            ReadySince = node.ReadySince,
            Conditions = ReadStoredList<KubernetesNodeConditionDto>(node.Conditions),
            Unschedulable = node.Unschedulable,
            UnschedulableSince = node.UnschedulableSince,
            ContainerRuntimeVersion = node.ContainerRuntimeVersion,
            PodCount = node.PodCount,
            PodCapacity = node.PodCapacity,
            LastReporterSeenAt = node.LastReporterSeenAt,
            KubeletSkew = KubernetesVersionSkew.IsKubeletSkewed(node.KubeletVersion, clusterVersion),
        };
    }

    /// <summary>
    /// Maps a cluster and its nodes to the list view.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="nodes">The cluster's nodes.</param>
    /// <param name="candidates">The cluster's reporter candidates.</param>
    /// <param name="machineNames">The names of the last snapshot sender and of the candidates' machines by machine id.</param>
    /// <param name="now">The time staleness is measured from.</param>
    public static KubernetesClusterSummaryDto ToSummary(
        KubernetesCluster cluster,
        IReadOnlyCollection<KubernetesNodeWithMachine> nodes,
        IReadOnlyCollection<KubernetesReporterCandidate> candidates,
        IReadOnlyDictionary<long, string> machineNames,
        DateTimeOffset now)
    {
        KubernetesClusterSummaryDto summary = new();
        FillSummary(summary, cluster, nodes, candidates, machineNames, now);

        return summary;
    }

    /// <summary>
    /// Maps a cluster and its nodes to the detail view, with the nodes ordered by name.
    /// </summary>
    /// <param name="cluster">The cluster.</param>
    /// <param name="nodes">The cluster's nodes.</param>
    /// <param name="candidates">The cluster's reporter candidates.</param>
    /// <param name="machineNames">The names of the last snapshot sender and of the candidates' machines by machine id.</param>
    /// <param name="machines">The cluster's candidates and eligible machines, as described for the page.</param>
    /// <param name="now">The time staleness is measured from.</param>
    public static KubernetesClusterDetailDto ToDetail(
        KubernetesCluster cluster,
        IReadOnlyCollection<KubernetesNodeWithMachine> nodes,
        IReadOnlyCollection<KubernetesReporterCandidate> candidates,
        IReadOnlyDictionary<long, string> machineNames,
        KubernetesReporterMachines machines,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(machines);

        KubernetesClusterDetailDto detail = new()
        {
            CaFingerprint = cluster.CaFingerprint,
            KubeSystemUid = cluster.KubeSystemUid,
            HasFingerprintMismatch = nodes.Any(n => n.Node.FingerprintMismatch),
            Nodes = nodes
                .OrderBy(n => n.Node.NodeName, StringComparer.Ordinal)
                .Select(n => ToNodeDto(n.Node, n.MachineName, cluster.ClusterVersion))
                .ToList(),
            Candidates = machines.Candidates.ToList(),
            EligibleMachines = machines.EligibleMachines.ToList(),
        };
        FillSummary(detail, cluster, nodes, candidates, machineNames, now);

        return detail;
    }

    private static void FillSummary(
        KubernetesClusterSummaryDto target,
        KubernetesCluster cluster,
        IReadOnlyCollection<KubernetesNodeWithMachine> nodes,
        IReadOnlyCollection<KubernetesReporterCandidate> candidates,
        IReadOnlyDictionary<long, string> machineNames,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(machineNames);

        int managed = nodes.Count(n => n.Node.MachineId != null);
        int ready = nodes.Count(n => string.Equals(n.Node.ReadyStatus, ReadyTrue, StringComparison.Ordinal));
        int notReady = nodes.Count(n => string.Equals(n.Node.ReadyStatus, ReadyFalse, StringComparison.Ordinal));

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
        target.ReadyCount = ready;
        target.NotReadyCount = notReady;
        target.UnknownCount = nodes.Count - ready - notReady;
        target.ReportingState = KubernetesReporting.ToWireValue(KubernetesReporting.Derive(cluster, candidates, now));
        target.ActiveReporterMachineId = cluster.ActiveReporterMachineId;
        target.ActiveReporterMachineName = cluster.ActiveReporterMachineId is null
            ? null
            : machineNames.GetValueOrDefault(cluster.ActiveReporterMachineId.Value);
        target.ClusterVersion = cluster.ClusterVersion;
        target.ReporterError = KubernetesReporting.ReporterErrorOf(cluster, candidates, machineNames);
        target.Truncated = cluster.Truncated;
        target.LastSnapshotReceivedAt = cluster.LastSnapshotReceivedAt;
        target.StaleSince = KubernetesReporting.IsStale(cluster, candidates, now) ? cluster.LastSnapshotReceivedAt : null;
        target.FirstSeenAt = cluster.FirstSeenAt;
        target.LastSeenAt = cluster.LastSeenAt;
    }

    /// <summary>
    /// Reads a list the projection stored as JSON text in camelCase. Text that is missing, is not JSON, or
    /// is not a list of the expected shape reads as an empty list.
    /// </summary>
    private static List<T> ReadStoredList<T>(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(stored, JsonDefaults.CamelCase) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
