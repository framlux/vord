// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IKubernetesInventoryRepository
{
    /// <inheritdoc/>
    public async Task<SnapshotOutcome> ApplyClusterSnapshotAsync(ClusterSnapshot snapshot, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await using DataConnectionTransaction transaction = await _db.BeginTransactionAsync(ct);
        await AcquireKubernetesTenantLockAsync(snapshot.TenantId, ct);

        SnapshotOutcome outcome = await ResolveAndApplySnapshotAsync(snapshot, now, ct);

        // The cursor moves in the same transaction as the write it accounts for, whatever the outcome,
        // so a crash can neither lose an applied snapshot's cursor advance nor advance past one that
        // was never applied.
        await UpsertKubernetesCursorAsync(snapshot.TelemetryId, now, ct);
        await transaction.CommitAsync(ct);

        return outcome;
    }

    /// <summary>
    /// Finds the snapshot's cluster, separates it from clusters that share its CA, merges in the
    /// clusters it turns out to be split across, fences the snapshot against the surviving cluster and
    /// applies it. The order is deliberate: fencing before the merges would compare the snapshot with
    /// the wrong epoch or transition count. Must run inside the tenant's transaction and lock.
    /// </summary>
    private async Task<SnapshotOutcome> ResolveAndApplySnapshotAsync(ClusterSnapshot snapshot, DateTimeOffset now, CancellationToken ct)
    {
        int tenantId = snapshot.TenantId;
        long machineId = snapshot.MachineId;

        Machine? machine = await _db.Machines
            .Where(m => (m.Id == machineId) && (m.TenantId == tenantId))
            .FirstOrDefaultAsync(ct);

        if ((machine == null) || machine.IsDeleted)
        {
            return SnapshotOutcome.MachineDeleted;
        }

        string? uid = string.IsNullOrEmpty(snapshot.KubeSystemUid) ? null : snapshot.KubeSystemUid;
        string? fingerprint = string.IsNullOrEmpty(snapshot.CaFingerprint) ? null : snapshot.CaFingerprint;
        bool isError = string.IsNullOrEmpty(snapshot.ReporterError) == false;

        KubernetesCluster? cluster = await FindSnapshotClusterAsync(snapshot, uid, fingerprint, ct);
        if (cluster == null)
        {
            // An error snapshot says nothing about the cluster, and one without a fingerprint cannot
            // be told apart from the tenant's other clusters, so neither may found one.
            if (isError || (fingerprint == null))
            {
                return SnapshotOutcome.Unresolved;
            }

            cluster = await CreateSnapshotClusterAsync(tenantId, fingerprint, uid, now, ct);
        }

        // A cluster's UID is never overwritten. A snapshot that names a different UID than the cluster
        // its sender was grouped into belongs to another cluster sharing the CA, and takes its sender
        // and the nodes it lists along.
        if ((uid != null) && (cluster.KubeSystemUid != null) && (string.Equals(cluster.KubeSystemUid, uid, StringComparison.Ordinal) == false))
        {
            KubernetesCluster? owner = await FindClusterByUidAsync(tenantId, uid, ct);
            if (owner == null)
            {
                if (isError || (fingerprint == null))
                {
                    return SnapshotOutcome.Unresolved;
                }

                owner = await CreateSnapshotClusterAsync(tenantId, fingerprint, uid, now, ct);
            }

            _logger.LogInformation(
                "Kubernetes cluster snapshot {TelemetryId} names UID {Uid}, not that of cluster {ClusterId}, so machine {MachineId} and the nodes it lists moved to cluster {OwnerId}",
                snapshot.TelemetryId, uid, cluster.Id, machineId, owner.Id);
            await MoveSnapshotMembersAsync(cluster, owner, snapshot, ct);
            cluster = owner;
        }

        if (uid != null)
        {
            long currentId = cluster.Id;
            KubernetesCluster? holder = await _db.KubernetesClusters
                .Where(c => (c.TenantId == tenantId) && (c.KubeSystemUid == uid) && (c.Id != currentId))
                .FirstOrDefaultAsync(ct);

            if (holder != null)
            {
                cluster = await MergeClustersAsync(cluster, holder, ct);
            }
        }

        if ((fingerprint != null) && (isError == false) && (snapshot.Nodes.Count > 0))
        {
            foreach (long relatedId in await FindOverlappingClusterIdsAsync(snapshot, fingerprint, cluster.Id, ct))
            {
                KubernetesCluster related = await LoadClusterAsync(tenantId, relatedId, ct);
                cluster = await MergeClustersAsync(cluster, related, ct);
            }
        }

        cluster = await LoadClusterAsync(tenantId, cluster.Id, ct);
        if (IsFencedBySurvivor(cluster, snapshot))
        {
            _logger.LogDebug(
                "Kubernetes cluster snapshot {TelemetryId} from machine {MachineId} is not newer than the state of cluster {ClusterId} and was ignored",
                snapshot.TelemetryId, machineId, cluster.Id);

            return SnapshotOutcome.Fenced;
        }

        await RecordAcceptedSnapshotAsync(cluster, snapshot, uid, fingerprint, isError, ct);
        await UpsertSnapshotCandidateAsync(cluster.Id, snapshot, ct);
        if (fingerprint != null)
        {
            await RecomputeFingerprintMismatchAsync(cluster.Id, fingerprint, ct);
        }

        if (isError)
        {
            return SnapshotOutcome.ErrorRecorded;
        }

        await UpsertSnapshotNodesAsync(cluster.Id, snapshot, ct);
        if (snapshot.Truncated == false)
        {
            await RemoveNodesAbsentFromSnapshotAsync(cluster.Id, snapshot, ct);
        }

        return SnapshotOutcome.Accepted;
    }

    /// <summary>
    /// Resolves the cluster a snapshot belongs to: the sender's candidate row, else the sender's node,
    /// else the cluster holding the snapshot's UID, else the one UID-less cluster holding its CA
    /// fingerprint. Returns nothing when none of them applies.
    /// </summary>
    private async Task<KubernetesCluster?> FindSnapshotClusterAsync(
        ClusterSnapshot snapshot, string? uid, string? fingerprint, CancellationToken ct)
    {
        int tenantId = snapshot.TenantId;
        long machineId = snapshot.MachineId;

        KubernetesReporterCandidate? candidate = await _db.KubernetesReporterCandidates
            .Where(c => (c.TenantId == tenantId) && (c.MachineId == machineId))
            .FirstOrDefaultAsync(ct);

        if (candidate != null)
        {
            long candidateClusterId = candidate.ClusterId;
            KubernetesCluster? ofCandidate = await _db.KubernetesClusters
                .Where(c => (c.TenantId == tenantId) && (c.Id == candidateClusterId))
                .FirstOrDefaultAsync(ct);

            if (ofCandidate != null)
            {
                return ofCandidate;
            }
        }

        KubernetesNode? node = await _db.KubernetesNodes
            .Where(n => (n.TenantId == tenantId) && (n.MachineId == machineId))
            .FirstOrDefaultAsync(ct);

        if (node != null)
        {
            long nodeClusterId = node.ClusterId;
            KubernetesCluster? ofNode = await _db.KubernetesClusters
                .Where(c => (c.TenantId == tenantId) && (c.Id == nodeClusterId))
                .FirstOrDefaultAsync(ct);

            if (ofNode != null)
            {
                return ofNode;
            }
        }

        if (uid != null)
        {
            KubernetesCluster? ofUid = await FindClusterByUidAsync(tenantId, uid, ct);
            if (ofUid != null)
            {
                return ofUid;
            }
        }

        if (fingerprint == null)
        {
            return null;
        }

        // Only a CA held by exactly one cluster without a UID identifies it; several leave the snapshot
        // to found a cluster of its own.
        List<KubernetesCluster> uidless = await _db.KubernetesClusters
            .Where(c => (c.TenantId == tenantId) && (c.CaFingerprint == fingerprint) && (c.KubeSystemUid == null))
            .OrderBy(c => c.Id)
            .Take(2)
            .ToListAsync(ct);

        return (uidless.Count == 1) ? uidless[0] : null;
    }

    private async Task<KubernetesCluster?> FindClusterByUidAsync(int tenantId, string uid, CancellationToken ct)
    {
        return await _db.KubernetesClusters
            .Where(c => (c.TenantId == tenantId) && (c.KubeSystemUid == uid))
            .FirstOrDefaultAsync(ct);
    }

    private async Task<KubernetesCluster> LoadClusterAsync(int tenantId, long clusterId, CancellationToken ct)
    {
        return await _db.KubernetesClusters.FirstAsync(c => (c.TenantId == tenantId) && (c.Id == clusterId), ct);
    }

    /// <summary>
    /// Founds a cluster for a snapshot. Its distro is unknown until a local report names one, which the
    /// local report path then takes over.
    /// </summary>
    private async Task<KubernetesCluster> CreateSnapshotClusterAsync(
        int tenantId, string fingerprint, string? uid, DateTimeOffset now, CancellationToken ct)
    {
        KubernetesCluster created = new()
        {
            TenantId = tenantId,
            CaFingerprint = fingerprint,
            Distro = UnknownKubernetesDistro,
            FirstSeenAt = now,
            LastSeenAt = now,
            KubeSystemUid = uid,
        };
        created.Id = await _db.InsertWithInt64IdentityAsync(created, token: ct);

        return created;
    }

    /// <summary>
    /// Moves the sender's candidate row and the nodes the snapshot lists from the cluster its sender was
    /// grouped into to the cluster of the snapshot's own UID. A node the target already has by name is
    /// combined with the moved one.
    /// </summary>
    private async Task MoveSnapshotMembersAsync(
        KubernetesCluster from, KubernetesCluster to, ClusterSnapshot snapshot, CancellationToken ct)
    {
        int tenantId = snapshot.TenantId;
        long machineId = snapshot.MachineId;
        long fromId = from.Id;
        long toId = to.Id;

        await _db.KubernetesReporterCandidates
            .Where(c => (c.TenantId == tenantId) && (c.MachineId == machineId) && (c.ClusterId == fromId))
            .Set(c => c.ClusterId, toId)
            .UpdateAsync(ct);

        List<string> names = snapshot.Nodes.Select(n => n.Name).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
        {
            return;
        }

        List<KubernetesNode> moving = await LoadNodesByNameAsync(fromId, names, ct);

        Dictionary<string, KubernetesNode> targetByName = await NodesByNameAsync(toId, ct);
        foreach (KubernetesNode node in moving)
        {
            await MoveNodeAsync(node, toId, targetByName, ct);
        }

        await RecomputeFingerprintMismatchAsync(toId, to.CaFingerprint, ct);
    }

    private async Task<Dictionary<string, KubernetesNode>> NodesByNameAsync(long clusterId, CancellationToken ct)
    {
        List<KubernetesNode> nodes = await _db.KubernetesNodes.Where(n => n.ClusterId == clusterId).ToListAsync(ct);

        return nodes.ToDictionary(n => n.NodeName, StringComparer.Ordinal);
    }

    /// <summary>
    /// Reports whether the cluster has accepted a snapshot the new one must not replace. A newer Lease
    /// epoch always passes, because uninstall deletes the Lease and the next one restarts the transition
    /// count. Within an epoch a lower count is stale, and an equal one passes only when it is strictly
    /// newer by (receipt time, telemetry id) than the last accepted snapshot.
    /// </summary>
    private static bool IsFencedBySurvivor(KubernetesCluster cluster, ClusterSnapshot snapshot)
    {
        if (cluster.LeaseCreatedAt == null)
        {
            return false;
        }

        int byEpoch = snapshot.LeaseCreatedAt.CompareTo(cluster.LeaseCreatedAt.Value);
        if (byEpoch != 0)
        {
            return byEpoch < 0;
        }

        if (snapshot.LeaseTransitions != cluster.LeaseTransitions)
        {
            return snapshot.LeaseTransitions < cluster.LeaseTransitions;
        }

        if (cluster.LastSnapshotReceivedAt == null)
        {
            return false;
        }

        int byTime = snapshot.ServerReceivedAt.CompareTo(cluster.LastSnapshotReceivedAt.Value);
        if (byTime != 0)
        {
            return byTime < 0;
        }

        return snapshot.TelemetryId <= (cluster.LastSnapshotTelemetryId ?? long.MinValue);
    }

    /// <summary>
    /// Writes what an accepted snapshot says about the cluster: the Lease state and the sender as the
    /// active reporter, the UID and CA fingerprint when it carries them, and either its error or, for a
    /// snapshot of the cluster, the details it reports. An error leaves the last reported details alone.
    /// A snapshot of the cluster moves its last-seen time forward to the snapshot's receipt time, never
    /// back; an error observed nothing of the cluster and leaves it.
    /// </summary>
    private async Task RecordAcceptedSnapshotAsync(
        KubernetesCluster cluster, ClusterSnapshot snapshot, string? uid, string? fingerprint, bool isError, CancellationToken ct)
    {
        long clusterId = cluster.Id;
        long machineId = snapshot.MachineId;
        string? leaseUid = snapshot.LeaseUid;
        DateTimeOffset leaseCreatedAt = snapshot.LeaseCreatedAt;
        long leaseTransitions = snapshot.LeaseTransitions;
        DateTimeOffset receivedAt = snapshot.ServerReceivedAt;
        long telemetryId = snapshot.TelemetryId;
        string? newUid = uid ?? cluster.KubeSystemUid;
        string newFingerprint = fingerprint ?? cluster.CaFingerprint;
        string? reporterError = isError ? snapshot.ReporterError : null;
        string? clusterVersion = isError ? cluster.ClusterVersion : snapshot.ClusterVersion;
        bool roleOutdated = isError ? cluster.RoleOutdated : snapshot.RoleOutdated;
        bool truncated = isError ? cluster.Truncated : snapshot.Truncated;
        DateTimeOffset lastSeenAt = (isError || (receivedAt <= cluster.LastSeenAt)) ? cluster.LastSeenAt : receivedAt;

        await _db.KubernetesClusters
            .Where(c => c.Id == clusterId)
            .Set(c => c.LastSeenAt, lastSeenAt)
            .Set(c => c.LeaseUid, leaseUid)
            .Set(c => c.LeaseCreatedAt, (DateTimeOffset?)leaseCreatedAt)
            .Set(c => c.LeaseTransitions, leaseTransitions)
            .Set(c => c.LastSnapshotReceivedAt, (DateTimeOffset?)receivedAt)
            .Set(c => c.LastSnapshotTelemetryId, (long?)telemetryId)
            .Set(c => c.ActiveReporterMachineId, (long?)machineId)
            .Set(c => c.KubeSystemUid, newUid)
            .Set(c => c.CaFingerprint, newFingerprint)
            .Set(c => c.ReporterError, reporterError)
            .Set(c => c.ClusterVersion, clusterVersion)
            .Set(c => c.RoleOutdated, roleOutdated)
            .Set(c => c.Truncated, truncated)
            .UpdateAsync(ct);
    }

    /// <summary>
    /// Keeps the sender a candidate of the cluster: an accepted snapshot proves it reports for the
    /// cluster even when its own local report lags. The candidate row is dated by the snapshot's receipt
    /// time only when that is newer than what it holds, so a local report received after the snapshot is
    /// never taken for an older one. The row carries the snapshot's error, or none.
    /// </summary>
    private async Task UpsertSnapshotCandidateAsync(long clusterId, ClusterSnapshot snapshot, CancellationToken ct)
    {
        int tenantId = snapshot.TenantId;
        long machineId = snapshot.MachineId;
        string? reporterError = string.IsNullOrEmpty(snapshot.ReporterError) ? null : snapshot.ReporterError;

        KubernetesReporterCandidate? existing = await _db.KubernetesReporterCandidates
            .Where(c => (c.TenantId == tenantId) && (c.MachineId == machineId))
            .FirstOrDefaultAsync(ct);

        if (existing == null)
        {
            await _db.InsertAsync(new KubernetesReporterCandidate
            {
                TenantId = tenantId,
                ClusterId = clusterId,
                MachineId = machineId,
                ReporterError = reporterError,
                LastReportAt = snapshot.ServerReceivedAt,
            }, token: ct);

            return;
        }

        DateTimeOffset lastReportAt = IsNewerThan(existing.LastReportAt, snapshot.ServerReceivedAt)
            ? existing.LastReportAt
            : snapshot.ServerReceivedAt;

        await _db.KubernetesReporterCandidates
            .Where(c => (c.TenantId == tenantId) && (c.MachineId == machineId))
            .Set(c => c.ClusterId, clusterId)
            .Set(c => c.ReporterError, reporterError)
            .Set(c => c.LastReportAt, lastReportAt)
            .UpdateAsync(ct);
    }

    /// <summary>
    /// Recomputes every node's fingerprint mismatch flag against the cluster's CA: a node is flagged
    /// when it has reported a fingerprint and it differs.
    /// </summary>
    private async Task RecomputeFingerprintMismatchAsync(long clusterId, string fingerprint, CancellationToken ct)
    {
        await _db.KubernetesNodes
            .Where(n => (n.ClusterId == clusterId)
                && (n.FingerprintMismatch == false)
                && (n.ReportedCaFingerprint != null)
                && (n.ReportedCaFingerprint != fingerprint))
            .Set(n => n.FingerprintMismatch, true)
            .UpdateAsync(ct);

        await _db.KubernetesNodes
            .Where(n => (n.ClusterId == clusterId)
                && n.FingerprintMismatch
                && ((n.ReportedCaFingerprint == null) || (n.ReportedCaFingerprint == fingerprint)))
            .Set(n => n.FingerprintMismatch, false)
            .UpdateAsync(ct);
    }
}
