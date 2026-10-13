// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for the Kubernetes inventory: the clusters a tenant's machines are grouped into, the
/// nodes inside them, and the projection that builds both from machines' local reports.
/// </summary>
public interface IKubernetesInventoryRepository
{
    /// <summary>
    /// Returns the last telemetry id the projection has applied or skipped, or null before it has
    /// processed anything.
    /// </summary>
    /// <param name="ct">A cancellation token.</param>
    Task<long?> GetProjectionCursorAsync(CancellationToken ct);

    /// <summary>
    /// Advances the projection cursor without applying a row, for rows the projection skips. The cursor
    /// only ever moves forward: a position at or below the stored one changes nothing.
    /// </summary>
    /// <param name="position">The telemetry id to record.</param>
    /// <param name="now">The time to record as the cursor's last advance.</param>
    /// <param name="ct">A cancellation token.</param>
    Task SetProjectionCursorAsync(long position, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Returns the next batch of Kubernetes telemetry rows after a cursor, in id order. Only rows
    /// received after <paramref name="receivedAfter"/> and accepted by the server no later than
    /// <paramref name="visibleBefore"/> are returned, so a row that commits late behind the cursor is
    /// still picked up on a later pass.
    /// </summary>
    /// <param name="afterId">Only rows with a greater id are returned.</param>
    /// <param name="receivedAfter">The start of the read window, exclusive.</param>
    /// <param name="visibleBefore">The latest server receipt time of a row that may be read, inclusive.</param>
    /// <param name="batchSize">The maximum number of rows to return.</param>
    /// <param name="ct">A cancellation token.</param>
    Task<IReadOnlyList<MachineTelemetry>> GetKubernetesTelemetryBatchAsync(
        long afterId, DateTimeOffset receivedAfter, DateTimeOffset visibleBefore, int batchSize, CancellationToken ct);

    /// <summary>
    /// Applies one local report in its own transaction under the tenant's Kubernetes lock: resolves the
    /// machine into a cluster by established membership, through a node or a reporter candidate row, or
    /// by CA fingerprint (a machine whose node reports the CA of exactly one other cluster moves to
    /// it), links or updates its node, refreshes the cluster's distro, re-keys the cluster when a CA
    /// rotation has finished, and advances the projection cursor to the report's telemetry id in the
    /// same transaction whatever the outcome.
    /// <para>
    /// A report whose node name is only the hostname applies to the node the cluster reporter already
    /// lists for the machine's <c>/etc/machine-id</c>, when exactly one does. A node the cluster
    /// reporter has seen is unlinked from its machine, never deleted, when the machine moves to a
    /// different node. A machine that reports itself enrolled is kept as a reporter candidate of the
    /// cluster its node belongs to, with the error its own reporter reported, and a machine that does
    /// not has no candidate row. A host with no kubelet is never a node, but an enrolled one is still
    /// made a candidate of the cluster it is already a candidate of, or of the one cluster that holds
    /// its CA, and no cluster is ever created for it.
    /// </para>
    /// The method opens that transaction itself, so it must not be called inside one: a transaction
    /// already open on the same connection would make the nested begin fail.
    /// </summary>
    /// <param name="report">The report to apply.</param>
    /// <param name="now">The time to record as the cursor's last advance.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>What the report did to the inventory.</returns>
    Task<LocalReportOutcome> ApplyLocalNodeReportAsync(LocalNodeReport report, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Applies one cluster snapshot from the holder of a cluster's reporter Lease in its own
    /// transaction under the tenant's Kubernetes lock, and advances the projection cursor to the
    /// snapshot's telemetry id in the same transaction whatever the outcome.
    /// <list type="number">
    /// <item>
    /// The cluster is resolved from the sender's candidate row, else its node row, else the
    /// snapshot's UID, else the one UID-less cluster holding its CA fingerprint. No cluster is ever
    /// created from an error snapshot or one without a fingerprint.
    /// </item>
    /// <item>
    /// A UID is never overwritten: a cluster that holds a different UID is left as it is, and the
    /// snapshot, the sender's candidate row and the nodes it lists move to the cluster of its own UID.
    /// </item>
    /// <item>
    /// A second cluster holding the UID is merged in, and so is every UID-less cluster that is
    /// fingerprint-related (it holds the fingerprint, or one of its nodes reported it within the last
    /// hour) and shares a node name with the snapshot. The cluster with the lower id survives.
    /// </item>
    /// <item>
    /// The surviving cluster then fences the snapshot per Lease epoch: an older epoch, a lower
    /// transition count within the epoch, or an equal count that is not newer by (receipt time, id)
    /// is ignored. A newer epoch starts the comparison over.
    /// </item>
    /// <item>
    /// An accepted snapshot records the Lease state and the sender as the active reporter and keeps
    /// the sender a candidate. An error snapshot only records its error. Any other snapshot upserts
    /// its nodes, links them to machines by machine id when that is unambiguous, and, unless it is
    /// truncated, removes nodes it no longer lists that no local report has described recently.
    /// </item>
    /// </list>
    /// The method opens its own transaction, so it must not be called inside one.
    /// </summary>
    /// <param name="snapshot">The snapshot to apply.</param>
    /// <param name="now">The time to record as the cursor's last advance and as the creation time of a cluster the snapshot founds.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>What the snapshot did to the inventory.</returns>
    Task<SnapshotOutcome> ApplyClusterSnapshotAsync(ClusterSnapshot snapshot, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Collects stale Kubernetes inventory one tenant at a time, each in its own transaction under
    /// that tenant's lock. The method opens those transactions itself, so it must not be called
    /// inside one.
    /// <list type="bullet">
    /// <item>A candidate whose machine has sent no local report for seven days is deleted.</item>
    /// <item>
    /// A node is deleted when it has had neither a local report nor a cluster reporter sighting for
    /// seven days, so an outage of this server or a paused holder never deletes nodes that no agent
    /// reports on.
    /// </item>
    /// <item>
    /// A cluster with reporter state, no candidates and no snapshot newer than
    /// <see cref="KubernetesReportingPolicy.StaleThreshold"/> goes back to its local state: nodes no
    /// local report ever described and nodes no machine is linked to are deleted, the other nodes lose
    /// their reporter columns, and the cluster loses its reporter columns and Lease epoch. A cluster
    /// still receiving snapshots is never reverted.
    /// </item>
    /// <item>A cluster with no nodes and no candidates is deleted.</item>
    /// </list>
    /// </summary>
    /// <param name="now">The time staleness is measured from.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The total number of candidate, node and cluster rows deleted.</returns>
    Task<int> CollectGarbageAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Returns every Kubernetes cluster the tenant has, oldest first.
    /// </summary>
    /// <param name="tenantId">The tenant whose clusters are listed.</param>
    /// <param name="ct">A cancellation token.</param>
    Task<IReadOnlyList<KubernetesCluster>> ListClustersAsync(int tenantId, CancellationToken ct);

    /// <summary>
    /// Returns one of the tenant's clusters, or null when the id is unknown or belongs to another
    /// tenant.
    /// </summary>
    /// <param name="tenantId">The tenant the cluster must belong to.</param>
    /// <param name="clusterId">The cluster id.</param>
    /// <param name="ct">A cancellation token.</param>
    Task<KubernetesCluster?> GetClusterAsync(int tenantId, long clusterId, CancellationToken ct);

    /// <summary>
    /// Returns the nodes of the given clusters, each with the name of the machine it is linked to.
    /// Only nodes of the tenant are returned, so an id belonging to another tenant's cluster matches
    /// nothing.
    /// </summary>
    /// <param name="tenantId">The tenant the clusters must belong to.</param>
    /// <param name="clusterIds">The clusters whose nodes are wanted.</param>
    /// <param name="ct">A cancellation token.</param>
    Task<IReadOnlyList<KubernetesNodeWithMachine>> ListNodesAsync(
        int tenantId, IReadOnlyCollection<long> clusterIds, CancellationToken ct);

    /// <summary>
    /// Sets or clears the operator-chosen name of one of the tenant's clusters. Takes the tenant's
    /// Kubernetes lock, so it must run inside the caller's transaction.
    /// </summary>
    /// <param name="tenantId">The tenant the cluster must belong to.</param>
    /// <param name="clusterId">The cluster to rename.</param>
    /// <param name="displayName">The new name, or null to return to the derived name.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>True when the cluster exists in the tenant, false otherwise.</returns>
    Task<bool> RenameClusterAsync(int tenantId, long clusterId, string? displayName, CancellationToken ct);

    /// <summary>
    /// Returns the node a machine is linked to, or null when it is not linked to one.
    /// </summary>
    /// <param name="tenantId">The tenant the machine and node must belong to.</param>
    /// <param name="machineId">The machine id.</param>
    /// <param name="ct">A cancellation token.</param>
    Task<KubernetesNode?> GetNodeForMachineAsync(int tenantId, long machineId, CancellationToken ct);

    /// <summary>
    /// Returns the reporter candidate row of a machine, or null when the machine is not a candidate of
    /// any cluster or belongs to another tenant.
    /// </summary>
    /// <param name="tenantId">The tenant the machine and candidate row must belong to.</param>
    /// <param name="machineId">The machine id.</param>
    /// <param name="ct">A cancellation token.</param>
    Task<KubernetesReporterCandidate?> GetCandidateForMachineAsync(int tenantId, long machineId, CancellationToken ct);

    /// <summary>
    /// Returns the reporter candidates of the given clusters, ordered by cluster and then machine.
    /// Only candidates of the tenant are returned, so an id belonging to another tenant's cluster
    /// matches nothing.
    /// </summary>
    /// <param name="tenantId">The tenant the clusters must belong to.</param>
    /// <param name="clusterIds">The clusters whose candidates are wanted.</param>
    /// <param name="ct">A cancellation token.</param>
    Task<IReadOnlyList<KubernetesReporterCandidate>> ListCandidatesAsync(
        int tenantId, IReadOnlyCollection<long> clusterIds, CancellationToken ct);

    /// <summary>
    /// Returns every machine of the tenant that is a node or a reporter candidate of a cluster other than
    /// <paramref name="clusterId"/>, in one query that carries no list of machines. A machine that is also
    /// linked to <paramref name="clusterId"/> is still returned, because this reports only its other
    /// links; the caller sets its own cluster's members aside. Only the tenant's own rows are read, and a
    /// node that no machine is linked to adds nothing.
    /// </summary>
    /// <param name="tenantId">The tenant the nodes and candidate rows must belong to.</param>
    /// <param name="clusterId">The cluster being considered; its own rows do not count.</param>
    /// <param name="ct">A cancellation token.</param>
    Task<IReadOnlyCollection<long>> GetMachineIdsLinkedToOtherClustersAsync(
        int tenantId, long clusterId, CancellationToken ct);

    /// <summary>
    /// Returns the names of the given machines in one query. Only machines of the tenant are
    /// returned, so an id belonging to another tenant's machine is absent from the result, as is an
    /// unknown id.
    /// </summary>
    /// <param name="tenantId">The tenant the machines must belong to.</param>
    /// <param name="machineIds">The machines whose names are wanted.</param>
    /// <param name="ct">A cancellation token.</param>
    Task<IReadOnlyDictionary<long, string>> GetMachineNamesInTenantAsync(
        int tenantId, IReadOnlyCollection<long> machineIds, CancellationToken ct);

    /// <summary>
    /// Returns the payload of the latest local node report (telemetry type 14) of each of the given
    /// machines, in one query for however many machines are asked about. The latest report is the one
    /// the server accepted last, with the higher row id winning a tie, and only reports the server
    /// accepted after <paramref name="serverReceivedAfter"/> are considered, so an agent with a skewed
    /// clock can neither hide a fresh report nor keep a stale one alive. Only the tenant's own rows are
    /// read and a machine with no such row is absent from the result. Never reads any other telemetry type.
    /// </summary>
    /// <param name="tenantId">The tenant the telemetry rows must belong to.</param>
    /// <param name="machineIds">The machines whose latest report is wanted.</param>
    /// <param name="serverReceivedAfter">The start of the read window on the server's clock, exclusive.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The raw JSON payload of each machine's latest report, keyed by machine id.</returns>
    Task<IReadOnlyDictionary<long, string>> GetLatestKubernetesNodePayloadsAsync(
        int tenantId, IReadOnlyCollection<long> machineIds, DateTimeOffset serverReceivedAfter, CancellationToken ct);

    /// <summary>
    /// Returns the tenant's clusters that hold a CA fingerprint, oldest first. Several can share one
    /// fingerprint.
    /// </summary>
    /// <param name="tenantId">The tenant whose clusters are searched.</param>
    /// <param name="caFingerprint">The lowercase hex SHA-256 CA fingerprint.</param>
    /// <param name="ct">A cancellation token.</param>
    Task<IReadOnlyList<KubernetesCluster>> ListClustersByFingerprintAsync(int tenantId, string caFingerprint, CancellationToken ct);

    /// <summary>
    /// Takes a transaction-scoped advisory lock covering all of this tenant's Kubernetes inventory
    /// writes, so the projection, garbage collection and operator edits never interleave on one
    /// tenant. The caller must be inside a transaction: the lock releases when that transaction ends.
    /// A no-op on providers without advisory locks, where writes are already serialized.
    /// </summary>
    /// <param name="tenantId">The tenant whose inventory is about to be written.</param>
    /// <param name="ct">A cancellation token.</param>
    Task AcquireKubernetesTenantLockAsync(int tenantId, CancellationToken ct);
}
