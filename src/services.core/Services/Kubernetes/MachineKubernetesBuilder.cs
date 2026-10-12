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
    /// Builds the block, or returns null when the machine has neither a recent local report nor a
    /// linked node. A linked node decides the cluster. Without one, the cluster is the single cluster
    /// of the tenant that holds the report's CA fingerprint, which places a host that runs no kubelet.
    /// </summary>
    /// <param name="repo">The Kubernetes inventory repository.</param>
    /// <param name="tenantId">The tenant that owns the machine.</param>
    /// <param name="machineId">The machine.</param>
    /// <param name="machineName">The machine's name, shown on the machine's own node.</param>
    /// <param name="rawPayload">
    /// The machine's latest local report from the past seven days, if any. It is normalised the same way
    /// the projection normalises it, and a report the projection would refuse is treated as absent.
    /// </param>
    /// <param name="ct">A cancellation token.</param>
    public static async Task<MachineKubernetesDto?> BuildAsync(
        IKubernetesInventoryRepository repo,
        int tenantId,
        long machineId,
        string machineName,
        KubernetesNodePayload? rawPayload,
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

        if ((payload == null) && (node == null))
        {
            return null;
        }

        MachineKubernetesDto block = new()
        {
            Local = payload == null ? null : ToLocalDto(payload),
        };

        if ((node != null) && (cluster != null))
        {
            block.Node = KubernetesClusterMapper.ToNodeDto(node, machineName);
            block.Grouping = KubernetesGrouping.Grouped;
            await PlaceInClusterAsync(block, repo, tenantId, cluster, ct);

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
