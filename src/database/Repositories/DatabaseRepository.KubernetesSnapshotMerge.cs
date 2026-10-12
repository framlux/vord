// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IKubernetesInventoryRepository
{
    /// <summary>
    /// Returns the ids of the other clusters in the tenant that are the snapshot's cluster split in two:
    /// they have no UID, are related by CA (they hold the snapshot's fingerprint, or one of their nodes
    /// reported it within the last hour) and have a node the snapshot lists by name. A node name alone
    /// never qualifies, because default names such as <c>ubuntu</c> collide across a tenant's clusters.
    /// A node of the same name that is linked to a machine whose system id equals the listed node's
    /// machine id is a node of that name too, so it needs no case of its own.
    /// </summary>
    private async Task<List<long>> FindOverlappingClusterIdsAsync(
        ClusterSnapshot snapshot, string fingerprint, long clusterId, CancellationToken ct)
    {
        int tenantId = snapshot.TenantId;
        DateTimeOffset recentSince = snapshot.ServerReceivedAt - KubernetesRecentReportWindow;
        List<string> names = snapshot.Nodes.Select(n => n.Name).Distinct(StringComparer.Ordinal).ToList();

        return await _db.KubernetesClusters
            .Where(c => (c.TenantId == tenantId)
                && (c.Id != clusterId)
                && (c.KubeSystemUid == null)
                && ((c.CaFingerprint == fingerprint)
                    || _db.KubernetesNodes.Any(n => (n.ClusterId == c.Id) && (n.ReportedCaFingerprint == fingerprint) && (n.LastLocalReportAt >= recentSince)))
                && _db.KubernetesNodes.Any(n => (n.ClusterId == c.Id) && names.Contains(n.NodeName)))
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Merges two clusters of one real cluster into the one with the lower id, which keeps its id, its
    /// name (or takes the other's if it has none) and the newer of the two Lease states, and receives the
    /// other's nodes, candidates and UID. The other cluster is deleted before the survivor takes its UID,
    /// so the unique index over (tenant, UID) holds throughout.
    /// </summary>
    /// <returns>The surviving cluster as stored after the merge.</returns>
    private async Task<KubernetesCluster> MergeClustersAsync(KubernetesCluster first, KubernetesCluster second, CancellationToken ct)
    {
        int tenantId = first.TenantId;
        long survivorId = Math.Min(first.Id, second.Id);
        long otherId = Math.Max(first.Id, second.Id);
        KubernetesCluster survivor = await LoadClusterAsync(tenantId, survivorId, ct);
        KubernetesCluster other = await LoadClusterAsync(tenantId, otherId, ct);

        _logger.LogInformation(
            "Kubernetes cluster {OtherId} is the same cluster as {SurvivorId} and was merged into it",
            otherId, survivorId);

        Dictionary<string, KubernetesNode> survivorByName = await NodesByNameAsync(survivorId, ct);
        List<KubernetesNode> otherNodes = await _db.KubernetesNodes.Where(n => n.ClusterId == otherId).ToListAsync(ct);
        foreach (KubernetesNode node in otherNodes)
        {
            await MoveNodeAsync(node, survivorId, survivorByName, ct);
        }

        await _db.KubernetesReporterCandidates
            .Where(c => c.ClusterId == otherId)
            .Set(c => c.ClusterId, survivorId)
            .UpdateAsync(ct);

        await _db.KubernetesClusters.Where(c => c.Id == otherId).DeleteAsync(ct);

        // The Lease state, the reporter that sent it and the details of that snapshot travel together.
        KubernetesCluster leaseSource = (CompareLeaseState(other, survivor) > 0) ? other : survivor;
        string? clusterVersion = leaseSource.ClusterVersion ?? survivor.ClusterVersion ?? other.ClusterVersion;
        string? displayName = string.IsNullOrWhiteSpace(survivor.DisplayName) ? other.DisplayName : survivor.DisplayName;
        string distro = (string.Equals(survivor.Distro, UnknownKubernetesDistro, StringComparison.Ordinal)
            && (string.Equals(other.Distro, UnknownKubernetesDistro, StringComparison.Ordinal) == false))
            ? other.Distro
            : survivor.Distro;
        DateTimeOffset firstSeenAt = (other.FirstSeenAt < survivor.FirstSeenAt) ? other.FirstSeenAt : survivor.FirstSeenAt;
        DateTimeOffset lastSeenAt = (other.LastSeenAt > survivor.LastSeenAt) ? other.LastSeenAt : survivor.LastSeenAt;
        string? uid = survivor.KubeSystemUid ?? other.KubeSystemUid;
        string? leaseUid = leaseSource.LeaseUid;
        DateTimeOffset? leaseCreatedAt = leaseSource.LeaseCreatedAt;
        long leaseTransitions = leaseSource.LeaseTransitions;
        DateTimeOffset? lastSnapshotReceivedAt = leaseSource.LastSnapshotReceivedAt;
        long? lastSnapshotTelemetryId = leaseSource.LastSnapshotTelemetryId;
        long? activeReporter = leaseSource.ActiveReporterMachineId;
        string? reporterError = leaseSource.ReporterError;
        bool roleOutdated = leaseSource.RoleOutdated;
        bool truncated = leaseSource.Truncated;

        await _db.KubernetesClusters
            .Where(c => c.Id == survivorId)
            .Set(c => c.DisplayName, displayName)
            .Set(c => c.Distro, distro)
            .Set(c => c.FirstSeenAt, firstSeenAt)
            .Set(c => c.LastSeenAt, lastSeenAt)
            .Set(c => c.KubeSystemUid, uid)
            .Set(c => c.ClusterVersion, clusterVersion)
            .Set(c => c.ReporterError, reporterError)
            .Set(c => c.RoleOutdated, roleOutdated)
            .Set(c => c.Truncated, truncated)
            .Set(c => c.LeaseUid, leaseUid)
            .Set(c => c.LeaseCreatedAt, leaseCreatedAt)
            .Set(c => c.LeaseTransitions, leaseTransitions)
            .Set(c => c.LastSnapshotReceivedAt, lastSnapshotReceivedAt)
            .Set(c => c.LastSnapshotTelemetryId, lastSnapshotTelemetryId)
            .Set(c => c.ActiveReporterMachineId, activeReporter)
            .UpdateAsync(ct);

        await RecomputeFingerprintMismatchAsync(survivorId, survivor.CaFingerprint, ct);

        return await LoadClusterAsync(tenantId, survivorId, ct);
    }

    /// <summary>
    /// Orders two clusters by their Lease state: the newer Lease epoch first, then the larger transition
    /// count within an epoch, then the newer last accepted snapshot. A cluster that has accepted nothing
    /// is older than one that has.
    /// </summary>
    private static int CompareLeaseState(KubernetesCluster a, KubernetesCluster b)
    {
        int byEpoch = Nullable.Compare(a.LeaseCreatedAt, b.LeaseCreatedAt);
        if (byEpoch != 0)
        {
            return byEpoch;
        }

        int byTransitions = a.LeaseTransitions.CompareTo(b.LeaseTransitions);
        if (byTransitions != 0)
        {
            return byTransitions;
        }

        int byTime = Nullable.Compare(a.LastSnapshotReceivedAt, b.LastSnapshotReceivedAt);
        if (byTime != 0)
        {
            return byTime;
        }

        return Nullable.Compare(a.LastSnapshotTelemetryId, b.LastSnapshotTelemetryId);
    }

    /// <summary>
    /// Moves a node into another cluster. When the target already has a node of the same name the two are
    /// combined, because a cluster holds a node name once.
    /// </summary>
    private async Task MoveNodeAsync(
        KubernetesNode node, long targetClusterId, Dictionary<string, KubernetesNode> targetByName, CancellationToken ct)
    {
        if (targetByName.TryGetValue(node.NodeName, out KubernetesNode? kept) == false)
        {
            long nodeId = node.Id;
            await _db.KubernetesNodes
                .Where(n => n.Id == nodeId)
                .Set(n => n.ClusterId, targetClusterId)
                .UpdateAsync(ct);

            node.ClusterId = targetClusterId;
            targetByName[node.NodeName] = node;

            return;
        }

        await CombineNodesAsync(kept, node, ct);
    }

    /// <summary>
    /// Combines two rows of one node into the kept row. The fields that only the node's own machine
    /// reports come from the row with the newer local report, the fields only the cluster reporter
    /// reports come from the row with the newer sighting, and the kubelet version, which either may
    /// report, comes from the row that was updated last unless that row has none, in which case the other
    /// row's version is kept. The machine link comes from whichever row has one, the row with the newer
    /// local report first. The other row is deleted before the kept row takes its link, so the unique index
    /// over the link holds.
    /// </summary>
    private async Task CombineNodesAsync(KubernetesNode kept, KubernetesNode moving, CancellationToken ct)
    {
        KubernetesNode local = (Nullable.Compare(moving.LastLocalReportAt, kept.LastLocalReportAt) > 0) ? moving : kept;
        KubernetesNode otherLocal = ReferenceEquals(local, kept) ? moving : kept;
        KubernetesNode sighting = (Nullable.Compare(moving.LastReporterSeenAt, kept.LastReporterSeenAt) > 0) ? moving : kept;
        KubernetesNode freshest = (Nullable.Compare(LastActivityOf(moving), LastActivityOf(kept)) > 0) ? moving : kept;
        KubernetesNode otherActivity = ReferenceEquals(freshest, kept) ? moving : kept;
        long? machineId = local.MachineId ?? otherLocal.MachineId;
        long keptId = kept.Id;
        long movingId = moving.Id;

        await _db.KubernetesNodes.Where(n => n.Id == movingId).DeleteAsync(ct);
        await _db.KubernetesNodes
            .Where(n => n.Id == keptId)
            .Set(n => n.MachineId, machineId)
            .Set(n => n.ReportedCaFingerprint, local.ReportedCaFingerprint)
            .Set(n => n.FingerprintMismatch, local.FingerprintMismatch)
            .Set(n => n.LocalControlPlane, local.LocalControlPlane)
            .Set(n => n.LastLocalReportAt, local.LastLocalReportAt)
            .Set(n => n.KubeletVersion, freshest.KubeletVersion ?? otherActivity.KubeletVersion)
            .Set(n => n.NodeMachineId, sighting.NodeMachineId)
            .Set(n => n.Roles, sighting.Roles)
            .Set(n => n.ReadyStatus, sighting.ReadyStatus)
            .Set(n => n.ReadySince, sighting.ReadySince)
            .Set(n => n.Conditions, sighting.Conditions)
            .Set(n => n.Unschedulable, sighting.Unschedulable)
            .Set(n => n.UnschedulableSince, sighting.UnschedulableSince)
            .Set(n => n.ContainerRuntimeVersion, sighting.ContainerRuntimeVersion)
            .Set(n => n.PodCount, sighting.PodCount)
            .Set(n => n.PodCapacity, sighting.PodCapacity)
            .Set(n => n.LastReporterSeenAt, sighting.LastReporterSeenAt)
            .UpdateAsync(ct);
    }

    private static DateTimeOffset? LastActivityOf(KubernetesNode node)
    {
        if (node.LastLocalReportAt == null)
        {
            return node.LastReporterSeenAt;
        }

        if (node.LastReporterSeenAt == null)
        {
            return node.LastLocalReportAt;
        }

        return (node.LastLocalReportAt > node.LastReporterSeenAt) ? node.LastLocalReportAt : node.LastReporterSeenAt;
    }
}
