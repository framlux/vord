// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using Framlux.FleetManagement.Database.Repositories;
using Framlux.FleetManagement.Services.Core.Models.Kubernetes;
using Framlux.FleetManagement.Services.Core.Models.Telemetry;

namespace Framlux.FleetManagement.Services.Core.Kubernetes;

/// <summary>
/// Builds the Kubernetes block of a machine's detail view from its latest local report and the
/// tenant's clusters.
/// </summary>
internal static class MachineKubernetesBuilder
{
    /// <summary>
    /// Builds the block, or returns null when the machine has no recent local report, no linked node and no
    /// candidate row. A linked node decides the cluster. Without one, a candidate row does, which places a
    /// reporter that is no node, such as a k0s controller, even where its CA fingerprint is held by several
    /// clusters. Without either, the cluster is the single cluster of the tenant that holds the report's CA
    /// fingerprint, which places a host that runs no kubelet and is not yet enrolled.
    /// </summary>
    /// <param name="repo">The Kubernetes inventory repository.</param>
    /// <param name="tenantId">The tenant that owns the machine.</param>
    /// <param name="machineId">The machine.</param>
    /// <param name="machineName">The machine's name, shown on the machine's own node.</param>
    /// <param name="rawPayload">
    /// The machine's latest local report from the past seven days, if any. It is normalised the same way
    /// the projection normalises it, and a report the projection would refuse is treated as absent.
    /// </param>
    /// <param name="now">The time a snapshot's age is measured from, to tell whether the machine is the active reporter.</param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task<MachineKubernetesDto?> BuildAsync(
        IKubernetesInventoryRepository repo,
        int tenantId,
        long machineId,
        string machineName,
        KubernetesNodePayload? rawPayload,
        DateTimeOffset now,
        CancellationToken ct)
    {
        KubernetesNodePayload? payload = rawPayload == null ? null : KubernetesNodePayloadNormaliser.Normalise(rawPayload);
        KubernetesNode? node = await repo.GetNodeForMachineAsync(tenantId, machineId, ct);
        KubernetesCluster? cluster = null;

        if (node != null)
        {
            cluster = await repo.GetClusterAsync(tenantId, node.ClusterId, ct);
            if (cluster == null)
            {
                node = null;
            }
        }

        (KubernetesReporterCandidate Row, KubernetesCluster Cluster)? candidate = await FindCandidateAsync(
            repo, tenantId, machineId, cluster, ct);

        if ((payload == null) && (node == null) && (candidate == null))
        {
            return null;
        }

        MachineKubernetesDto block = new()
        {
            Local = payload == null ? null : ToLocalDto(payload),
            Candidate = candidate == null ? null : ToCandidateDto(candidate.Value.Row, candidate.Value.Cluster, machineId, now),
        };

        if ((node != null) && (cluster != null))
        {
            block.Node = KubernetesClusterMapper.ToNodeDto(node, machineName, cluster.ClusterVersion);
            block.Grouping = KubernetesGrouping.Grouped;
            await PlaceInClusterAsync(block, repo, tenantId, cluster, ct);

            return block;
        }

        if (candidate != null)
        {
            block.Grouping = KubernetesGrouping.Grouped;
            await PlaceInClusterAsync(block, repo, tenantId, candidate.Value.Cluster, ct);

            return block;
        }

        string fingerprint = payload!.CaFingerprint;
        if (string.IsNullOrEmpty(fingerprint))
        {
            block.Grouping = KubernetesGrouping.Unknown;

            return block;
        }

        IReadOnlyList<KubernetesCluster> holders = await repo.ListClustersByFingerprintAsync(tenantId, fingerprint, ct);
        if (holders.Count == 0)
        {
            block.Grouping = KubernetesGrouping.Ungrouped;
        }
        else if (holders.Count > 1)
        {
            block.Grouping = KubernetesGrouping.Ambiguous;
        }
        else
        {
            block.Grouping = KubernetesGrouping.Grouped;
            await PlaceInClusterAsync(block, repo, tenantId, holders[0], ct);
        }

        return block;
    }

    /// <summary>
    /// Finds the machine's candidate row with the cluster it names. A row whose cluster cannot be found is
    /// treated as absent: it cannot be shown without the cluster it belongs to.
    /// </summary>
    private static async Task<(KubernetesReporterCandidate Row, KubernetesCluster Cluster)?> FindCandidateAsync(
        IKubernetesInventoryRepository repo, int tenantId, long machineId, KubernetesCluster? nodeCluster, CancellationToken ct)
    {
        KubernetesReporterCandidate? row = await repo.GetCandidateForMachineAsync(tenantId, machineId, ct);
        if (row == null)
        {
            return null;
        }

        KubernetesCluster? cluster = (nodeCluster != null) && (nodeCluster.Id == row.ClusterId)
            ? nodeCluster
            : await repo.GetClusterAsync(tenantId, row.ClusterId, ct);

        return cluster == null ? null : (row, cluster);
    }

    private static MachineKubernetesCandidateDto ToCandidateDto(KubernetesReporterCandidate row, KubernetesCluster cluster, long machineId, DateTimeOffset now)
    {
        return new MachineKubernetesCandidateDto
        {
            ClusterId = row.ClusterId,
            IsActiveReporter = KubernetesReporting.IsActiveReporter(cluster, machineId, now),
            ReporterError = row.ReporterError,
            LastReportAt = row.LastReportAt,
        };
    }

    private static async Task PlaceInClusterAsync(
        MachineKubernetesDto block, IKubernetesInventoryRepository repo, int tenantId, KubernetesCluster cluster, CancellationToken ct)
    {
        block.ClusterId = cluster.Id;

        // The nodes only matter for the derived name, so a cluster an operator has named needs none.
        if (KubernetesClusterMapper.HasDisplayName(cluster))
        {
            block.ClusterName = cluster.DisplayName;

            return;
        }

        IReadOnlyList<KubernetesNodeWithMachine> nodes = await repo.ListNodesAsync(tenantId, [cluster.Id], ct);

        block.ClusterName = KubernetesClusterMapper.NameOf(cluster, nodes.Select(n => n.Node));
    }

    private static MachineKubernetesLocalDto ToLocalDto(KubernetesNodePayload payload)
    {
        return new MachineKubernetesLocalDto
        {
            Distro = payload.Distro,
            KubeletVersion = payload.KubeletVersion,
            NodeName = payload.HasKubelet ? payload.NodeName : null,
            NodeNameSource = payload.HasKubelet ? payload.NodeNameSource : null,
            HasKubelet = payload.HasKubelet,
            ControlPlane = payload.ControlPlane,
            AdminCredentialPresent = payload.AdminCredentialPresent,
            ReporterEnrolled = payload.ReporterEnrolled,
        };
    }
}
