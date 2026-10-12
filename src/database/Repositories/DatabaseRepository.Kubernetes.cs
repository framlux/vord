// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Enums;
using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IKubernetesInventoryRepository
{
    /// <summary>
    /// First key of the Kubernetes inventory advisory lock, keeping it distinct from every other
    /// advisory lock taken against this database.
    /// </summary>
    private const int KubernetesTenantLockNamespace = 0x4B38_5349;

    /// <summary>The projection is a single stream, so its cursor table holds one row with this key.</summary>
    private const int KubernetesCursorRowId = 1;

    /// <summary>The telemetry type of a machine's local Kubernetes node report.</summary>
    private const short KubernetesNodeTelemetryType = 14;

    /// <summary>The distro value a report carries when the host's layout matched no known distribution.</summary>
    private const string UnknownKubernetesDistro = "unknown";

    /// <summary>
    /// How recently a node must have reported for its reported fingerprint to count as evidence about
    /// its cluster: a node that reported within this window is taking part in a rotation or reinstall.
    /// </summary>
    private static readonly TimeSpan KubernetesRecentReportWindow = TimeSpan.FromHours(1);

    /// <summary>The node name source a report carries when the agent could only name the node after its hostname.</summary>
    private const string HostnameNodeNameSource = "hostname";

    /// <summary>
    /// How long garbage collection waits before removing a node that neither its own machine nor the
    /// cluster reporter has described, and a candidate whose machine has sent no local report.
    /// </summary>
    private static readonly TimeSpan KubernetesNodeStalenessWindow = TimeSpan.FromDays(7);

    /// <inheritdoc/>
    public async Task<long?> GetProjectionCursorAsync(CancellationToken ct)
    {
        List<long> positions = await _db.KubernetesInventoryProjectionCursors
            .Where(c => c.Id == KubernetesCursorRowId)
            .Select(c => c.Position)
            .ToListAsync(ct);

        return positions.Count == 0 ? null : positions[0];
    }

    /// <inheritdoc/>
    public async Task SetProjectionCursorAsync(long position, DateTimeOffset now, CancellationToken ct)
    {
        await UpsertKubernetesCursorAsync(position, now, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<MachineTelemetry>> GetKubernetesTelemetryBatchAsync(
        long afterId, DateTimeOffset receivedAfter, DateTimeOffset visibleBefore, int batchSize, CancellationToken ct)
    {
        // The telemetry type filter is a SQL literal rather than a parameter so the planner can prove
        // it implies the predicate of the partial index over the Kubernetes types; a parameter would
        // keep that index out of reach. Only rows that have been visible for the safety lag are read
        // (ServerReceivedAt <= visibleBefore), so a lower-id row that committed after the cursor passed
        // it is still picked up instead of being skipped forever.
        List<MachineTelemetry> rows = await _db.QueryToListAsync<MachineTelemetry>(
            """
            SELECT * FROM "MachineTelemetry"
            WHERE "TelemetryType" IN (14, 15) AND "Id" > @afterId AND "ReceivedAt" > @receivedAfter
              AND "ServerReceivedAt" <= @visibleBefore
            ORDER BY "Id" LIMIT @batchSize
            """,
            ct,
            new DataParameter("@afterId", afterId),
            new DataParameter("@receivedAfter", receivedAfter),
            new DataParameter("@visibleBefore", visibleBefore),
            new DataParameter("@batchSize", batchSize));

        return rows;
    }

    /// <inheritdoc/>
    public async Task AcquireKubernetesTenantLockAsync(int tenantId, CancellationToken ct)
    {
        // Uses the two-argument form so the first key namespaces this lock away from every other
        // advisory lock in the system and the second scopes it to one tenant.
        if (_db.DataProvider.Name.Contains("PostgreSQL"))
        {
            await _db.ExecuteAsync(
                "SELECT pg_advisory_xact_lock(@namespace, @tenantId)",
                ct,
                new DataParameter("@namespace", KubernetesTenantLockNamespace),
                new DataParameter("@tenantId", tenantId));
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<KubernetesCluster>> ListClustersAsync(int tenantId, CancellationToken ct)
    {
        List<KubernetesCluster> clusters = await _db.KubernetesClusters
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

        return clusters;
    }

    /// <inheritdoc/>
    public async Task<KubernetesCluster?> GetClusterAsync(int tenantId, long clusterId, CancellationToken ct)
    {
        return await _db.KubernetesClusters
            .Where(c => (c.TenantId == tenantId) && (c.Id == clusterId))
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<KubernetesNodeWithMachine>> ListNodesAsync(
        int tenantId, IReadOnlyCollection<long> clusterIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clusterIds);

        if (clusterIds.Count == 0)
        {
            return [];
        }

        // The machine is joined on the node's own tenant as well as its id, so a node never picks up the
        // name of a machine that belongs to someone else.
        List<long> ids = clusterIds.ToList();
        List<KubernetesNodeWithMachine> rows = await (
            from node in _db.KubernetesNodes
            where (node.TenantId == tenantId) && ids.Contains(node.ClusterId)
            from machine in _db.Machines
                .Where(m => (m.Id == node.MachineId) && (m.TenantId == node.TenantId))
                .DefaultIfEmpty()
            orderby node.ClusterId, node.Id
            select new KubernetesNodeWithMachine(node, machine.Name))
            .ToListAsync(ct);

        return rows;
    }

    /// <inheritdoc/>
    public async Task<bool> RenameClusterAsync(int tenantId, long clusterId, string? displayName, CancellationToken ct)
    {
        await AcquireKubernetesTenantLockAsync(tenantId, ct);

        int updated = await _db.KubernetesClusters
            .Where(c => (c.TenantId == tenantId) && (c.Id == clusterId))
            .Set(c => c.DisplayName, displayName)
            .UpdateAsync(ct);

        return updated > 0;
    }

    /// <inheritdoc/>
    public async Task<KubernetesNode?> GetNodeForMachineAsync(int tenantId, long machineId, CancellationToken ct)
    {
        return await _db.KubernetesNodes
            .Where(n => (n.TenantId == tenantId) && (n.MachineId == machineId))
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<KubernetesReporterCandidate?> GetCandidateForMachineAsync(int tenantId, long machineId, CancellationToken ct)
    {
        return await _db.KubernetesReporterCandidates
            .Where(c => (c.TenantId == tenantId) && (c.MachineId == machineId))
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<KubernetesReporterCandidate>> ListCandidatesAsync(
        int tenantId, IReadOnlyCollection<long> clusterIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clusterIds);

        if (clusterIds.Count == 0)
        {
            return [];
        }

        List<long> ids = clusterIds.ToList();
        List<KubernetesReporterCandidate> candidates = await _db.KubernetesReporterCandidates
            .Where(c => (c.TenantId == tenantId) && ids.Contains(c.ClusterId))
            .OrderBy(c => c.ClusterId)
            .ThenBy(c => c.MachineId)
            .ToListAsync(ct);

        return candidates;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyCollection<long>> GetMachineIdsLinkedToOtherClustersAsync(
        int tenantId, long clusterId, IReadOnlyCollection<long> machineIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(machineIds);

        if (machineIds.Count == 0)
        {
            return [];
        }

        List<long> ids = machineIds.Distinct().ToList();
        IQueryable<long> nodeMachines = _db.KubernetesNodes
            .Where(n => (n.TenantId == tenantId) && (n.ClusterId != clusterId) && (n.MachineId != null) && ids.Contains(n.MachineId.Value))
            .Select(n => n.MachineId!.Value);
        IQueryable<long> candidateMachines = _db.KubernetesReporterCandidates
            .Where(c => (c.TenantId == tenantId) && (c.ClusterId != clusterId) && ids.Contains(c.MachineId))
            .Select(c => c.MachineId);
        List<long> linked = await nodeMachines.Union(candidateMachines).ToListAsync(ct);

        return linked;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<long, string>> GetMachineNamesInTenantAsync(
        int tenantId, IReadOnlyCollection<long> machineIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(machineIds);

        if (machineIds.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        List<long> ids = machineIds.Distinct().ToList();
        Dictionary<long, string> names = await _db.Machines
            .Where(m => (m.TenantId == tenantId) && ids.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Name, ct);

        return names;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<long, string>> GetLatestKubernetesNodePayloadsAsync(
        int tenantId, IReadOnlyCollection<long> machineIds, DateTimeOffset serverReceivedAfter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(machineIds);

        if (machineIds.Count == 0)
        {
            return new Dictionary<long, string>();
        }

        long[] ids = machineIds.Distinct().ToArray();

        // ReceivedAt is derived from the agent's own clock, so it is no measure of recency: it only
        // bounds which partitions are read. Recency is ServerReceivedAt, the server's own clock, and
        // ingestion clamps ReceivedAt to a window of this many days, so no row the server-time bound
        // keeps can sit below the partition bound.
        DateTimeOffset partitionLowerBound = serverReceivedAfter.AddDays(-TelemetryPartitionPruningLookbackDays);
        List<MachineTelemetry> rows;

        if (_db.DataProvider.Name.Contains("PostgreSQL"))
        {
            // One statement for every machine asked about, however many: DISTINCT ON keeps the first row
            // of each machine in the requested order, which is the newest by server receipt time, with the
            // row id breaking a tie. The type is a SQL literal for the same reason as in the projector's
            // batch read.
            rows = await _db.QueryToListAsync<MachineTelemetry>(
                """
                SELECT DISTINCT ON ("MachineId") "MachineId", "Payload" FROM "MachineTelemetry"
                WHERE "TenantId" = @tenantId AND "TelemetryType" = 14 AND "MachineId" = ANY(@machineIds)
                  AND "ServerReceivedAt" > @serverReceivedAfter AND "ReceivedAt" > @partitionLowerBound
                ORDER BY "MachineId", "ServerReceivedAt" DESC, "Id" DESC
                """,
                ct,
                new DataParameter("@tenantId", tenantId),
                new DataParameter("@machineIds", ids),
                new DataParameter("@serverReceivedAfter", serverReceivedAfter),
                new DataParameter("@partitionLowerBound", partitionLowerBound));
        }
        else
        {
            // SQLite test path, which has no DISTINCT ON: the same newest-row-per-machine rule as a group-by.
            rows = await _db.MachineTelemetry
                .Where(t => (t.TenantId == tenantId)
                    && (t.TelemetryType == KubernetesNodeTelemetryType)
                    && ids.Contains(t.MachineId)
                    && (t.ServerReceivedAt > serverReceivedAfter)
                    && (t.ReceivedAt > partitionLowerBound))
                .GroupBy(t => t.MachineId)
                .Select(g => g.OrderByDescending(t => t.ServerReceivedAt).ThenByDescending(t => t.Id).First())
                .ToListAsync(ct);
        }

        return rows.ToDictionary(r => r.MachineId, r => r.Payload);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<KubernetesCluster>> ListClustersByFingerprintAsync(
        int tenantId, string caFingerprint, CancellationToken ct)
    {
        List<KubernetesCluster> clusters = await _db.KubernetesClusters
            .Where(c => (c.TenantId == tenantId) && (c.CaFingerprint == caFingerprint))
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

        return clusters;
    }

    /// <inheritdoc/>
    public async Task<LocalReportOutcome> ApplyLocalNodeReportAsync(LocalNodeReport report, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        await using DataConnectionTransaction transaction = await _db.BeginTransactionAsync(ct);
        await AcquireKubernetesTenantLockAsync(report.TenantId, ct);

        LocalReportOutcome outcome = await ResolveLocalNodeReportAsync(report, ct);

        // The cursor moves in the same transaction as the write it accounts for, whatever the outcome,
        // so a crash can neither lose an applied row's cursor advance nor advance past a row that was
        // never applied.
        await UpsertKubernetesCursorAsync(report.TelemetryId, now, ct);
        await transaction.CommitAsync(ct);

        return outcome;
    }

    /// <inheritdoc/>
    public async Task<int> CollectGarbageAsync(DateTimeOffset now, CancellationToken ct)
    {
        DateTimeOffset staleBefore = now - KubernetesNodeStalenessWindow;
        DateTimeOffset snapshotStaleBefore = now - KubernetesReportingPolicy.StaleThreshold;

        List<int> clusterTenants = await _db.KubernetesClusters.Select(c => c.TenantId).Distinct().ToListAsync(ct);
        List<int> nodeTenants = await _db.KubernetesNodes.Select(n => n.TenantId).Distinct().ToListAsync(ct);
        List<int> candidateTenants = await _db.KubernetesReporterCandidates.Select(c => c.TenantId).Distinct().ToListAsync(ct);
        List<int> tenantIds = clusterTenants.Union(nodeTenants).Union(candidateTenants).OrderBy(id => id).ToList();

        int deleted = 0;
        foreach (int tenantId in tenantIds)
        {
            await using DataConnectionTransaction transaction = await _db.BeginTransactionAsync(ct);
            await AcquireKubernetesTenantLockAsync(tenantId, ct);

            // Candidates go first, so a cluster whose last candidate just expired is judged for the
            // reporting-disabled revert in the same pass.
            int staleCandidates = await _db.KubernetesReporterCandidates
                .Where(c => (c.TenantId == tenantId) && (c.LastReportAt < staleBefore))
                .DeleteAsync(ct);

            // A node is only gone when nothing has described it for the whole window: neither a local
            // report from its own machine nor a sighting by the cluster reporter. A stalled holder or
            // an outage of this server therefore never removes nodes that no agent reports on.
            int staleNodes = await _db.KubernetesNodes
                .Where(n => (n.TenantId == tenantId)
                    && ((n.LastLocalReportAt == null) || (n.LastLocalReportAt < staleBefore))
                    && ((n.LastReporterSeenAt == null) || (n.LastReporterSeenAt < staleBefore)))
                .DeleteAsync(ct);

            int revertedNodes = await RevertClustersWithReportingDisabledAsync(tenantId, snapshotStaleBefore, ct);

            // Nodes and candidates go first so a cluster whose last node was just removed is removed in
            // the same pass.
            int emptyClusters = await _db.KubernetesClusters
                .Where(c => (c.TenantId == tenantId)
                    && (_db.KubernetesNodes.Any(n => n.ClusterId == c.Id) == false)
                    && (_db.KubernetesReporterCandidates.Any(k => k.ClusterId == c.Id) == false))
                .DeleteAsync(ct);

            await transaction.CommitAsync(ct);

            deleted += staleCandidates + staleNodes + revertedNodes + emptyClusters;
        }

        if (deleted > 0)
        {
            _logger.LogInformation("Kubernetes inventory garbage collection removed {Count} stale rows", deleted);
        }

        return deleted;
    }

    /// <summary>
    /// Reverts every cluster of the tenant that has reporter state, no candidate left and no snapshot
    /// newer than the staleness threshold to what local reports alone know: nodes that only the
    /// reporter described are deleted, the other nodes lose their reporter columns, and the cluster
    /// loses its reporter columns and its Lease epoch, because enabling reporting again creates a new
    /// Lease. A cluster that is still receiving snapshots is never touched, however far its
    /// candidates' local reports lag. Must run inside the tenant's transaction and lock.
    /// </summary>
    /// <returns>The number of node rows deleted.</returns>
    private async Task<int> RevertClustersWithReportingDisabledAsync(int tenantId, DateTimeOffset snapshotStaleBefore, CancellationToken ct)
    {
        List<long> disabled = await _db.KubernetesClusters
            .Where(c => (c.TenantId == tenantId)
                && (_db.KubernetesReporterCandidates.Any(k => k.ClusterId == c.Id) == false)
                && ((c.LastSnapshotReceivedAt == null) || (c.LastSnapshotReceivedAt < snapshotStaleBefore))
                && ((c.KubeSystemUid != null)
                    || (c.ClusterVersion != null)
                    || (c.ReporterError != null)
                    || c.RoleOutdated
                    || c.Truncated
                    || (c.LeaseTransitions != 0)
                    || (c.ActiveReporterMachineId != null)
                    || (c.LastSnapshotReceivedAt != null)
                    || (c.LastSnapshotTelemetryId != null)
                    || (c.LeaseUid != null)
                    || (c.LeaseCreatedAt != null)))
            .Select(c => c.Id)
            .ToListAsync(ct);

        if (disabled.Count == 0)
        {
            return 0;
        }

        int agentlessNodes = await _db.KubernetesNodes
            .Where(n => disabled.Contains(n.ClusterId) && (n.LastLocalReportAt == null))
            .DeleteAsync(ct);

        await _db.KubernetesNodes
            .Where(n => disabled.Contains(n.ClusterId))
            .Set(n => n.NodeMachineId, (string?)null)
            .Set(n => n.Roles, (string?)null)
            .Set(n => n.ReadyStatus, (string?)null)
            .Set(n => n.ReadySince, (DateTimeOffset?)null)
            .Set(n => n.Conditions, (string?)null)
            .Set(n => n.Unschedulable, false)
            .Set(n => n.UnschedulableSince, (DateTimeOffset?)null)
            .Set(n => n.ContainerRuntimeVersion, (string?)null)
            .Set(n => n.PodCount, (int?)null)
            .Set(n => n.PodCapacity, (int?)null)
            .Set(n => n.LastReporterSeenAt, (DateTimeOffset?)null)
            .UpdateAsync(ct);

        await _db.KubernetesClusters
            .Where(c => disabled.Contains(c.Id))
            .Set(c => c.KubeSystemUid, (string?)null)
            .Set(c => c.ClusterVersion, (string?)null)
            .Set(c => c.ReporterError, (string?)null)
            .Set(c => c.RoleOutdated, false)
            .Set(c => c.Truncated, false)
            .Set(c => c.ActiveReporterMachineId, (long?)null)
            .Set(c => c.LastSnapshotReceivedAt, (DateTimeOffset?)null)
            .Set(c => c.LastSnapshotTelemetryId, (long?)null)
            .Set(c => c.LeaseUid, (string?)null)
            .Set(c => c.LeaseCreatedAt, (DateTimeOffset?)null)
            .Set(c => c.LeaseTransitions, 0L)
            .UpdateAsync(ct);

        _logger.LogInformation(
            "Kubernetes clusters {ClusterIds} have no reporter candidates and no recent snapshot, so their reporter state was cleared",
            disabled);

        return agentlessNodes;
    }

    /// <summary>
    /// Resolves one report into the inventory: finds or creates the machine's cluster, links or updates
    /// its node, keeps the machine's reporter candidate row in step with what it reports, and re-keys
    /// the cluster when a CA rotation has finished. Must run inside the tenant's transaction and lock.
    /// </summary>
    private async Task<LocalReportOutcome> ResolveLocalNodeReportAsync(LocalNodeReport report, CancellationToken ct)
    {
        int tenantId = report.TenantId;
        long machineId = report.MachineId;

        Machine? machine = await _db.Machines
            .Where(m => (m.Id == machineId) && (m.TenantId == tenantId))
            .FirstOrDefaultAsync(ct);

        if ((machine == null) || machine.IsDeleted)
        {
            return LocalReportOutcome.MachineDeleted;
        }

        KubernetesReporterCandidate? candidate = await _db.KubernetesReporterCandidates
            .Where(c => (c.TenantId == tenantId) && (c.MachineId == machineId))
            .FirstOrDefaultAsync(ct);

        if ((candidate != null) && IsNewerThan(candidate.LastReportAt, report.ServerReceivedAt))
        {
            return LocalReportOutcome.Superseded;
        }

        if (report.HasKubelet == false)
        {
            return await ApplyReportOfHostWithoutKubeletAsync(report, candidate, ct);
        }

        KubernetesNode? existing = await _db.KubernetesNodes
            .Where(n => (n.TenantId == tenantId) && (n.MachineId == machineId))
            .FirstOrDefaultAsync(ct);

        if ((existing != null) && IsNewerThan(existing.LastLocalReportAt, report.ServerReceivedAt))
        {
            return LocalReportOutcome.Superseded;
        }

        KubernetesCluster cluster;
        LocalReportOutcome outcome = LocalReportOutcome.Grouped;
        long? memberClusterId = (existing != null) ? existing.ClusterId : candidate?.ClusterId;
        if (memberClusterId != null)
        {
            // Established membership, through a node or a reporter candidate row, wins over whatever
            // fingerprint the machine reports now, with one exception: a machine that reports the CA of
            // exactly one other cluster has moved there.
            long establishedClusterId = memberClusterId.Value;
            cluster = await _db.KubernetesClusters.FirstAsync(c => (c.Id == establishedClusterId) && (c.TenantId == tenantId), ct);

            KubernetesCluster? movedTo = await FindClusterTheMachineMovedToAsync(cluster, report.CaFingerprint, ct);
            if (movedTo != null)
            {
                _logger.LogInformation(
                    "Kubernetes node {NodeName} of machine {MachineId} moved from cluster {PreviousClusterId} to cluster {ClusterId}",
                    report.NodeName, machineId, cluster.Id, movedTo.Id);
                cluster = movedTo;
            }
        }
        else
        {
            (KubernetesCluster? resolved, LocalReportOutcome resolution) = await ResolveClusterForUnlinkedMachineAsync(report, ct);
            if (resolved == null)
            {
                return resolution;
            }

            cluster = resolved;
            outcome = resolution;
        }

        long clusterId = cluster.Id;

        // A name the agent could only take from the hostname is a guess. When the cluster reporter has
        // already listed a node of this machine under its real name, the report belongs to that row.
        KubernetesNode? target = null;
        if (string.Equals(report.NodeNameSource, HostnameNodeNameSource, StringComparison.Ordinal))
        {
            target = await FindReporterRowForHostnameFallbackAsync(machine, clusterId, ct);
        }

        if (target == null)
        {
            string reportedName = report.NodeName;
            target = await _db.KubernetesNodes
                .Where(n => (n.ClusterId == clusterId) && (n.NodeName == reportedName))
                .FirstOrDefaultAsync(ct);
        }

        string nodeName = (target != null) ? target.NodeName : report.NodeName;
        if ((target != null) && IsNewerThan(target.LastLocalReportAt, report.ServerReceivedAt))
        {
            return LocalReportOutcome.Superseded;
        }

        // The machine's previous link is cleared before the new one is set, which keeps the unique
        // machine link satisfied however the node moves. A different row than the machine's own means
        // its node was renamed inside its cluster, for example from its hostname to its certificate
        // name, or that the machine moved to another cluster. Either way the old row is replaced rather
        // than left behind as a second node, except that a row the cluster reporter has listed is never
        // deleted by a local report: the reporter still sees that node, so it only loses its machine.
        if ((existing != null) && ((target == null) || (existing.Id != target.Id)))
        {
            long existingId = existing.Id;
            if (existing.LastReporterSeenAt != null)
            {
                await _db.KubernetesNodes
                    .Where(n => n.Id == existingId)
                    .Set(n => n.MachineId, (long?)null)
                    .UpdateAsync(ct);
            }
            else
            {
                await _db.KubernetesNodes.Where(n => n.Id == existingId).DeleteAsync(ct);
            }
        }

        // A report that carries no fingerprint or version leaves what is stored. For a renamed node
        // the stored values are those of the row being replaced.
        KubernetesNode? stored = target ?? existing;
        string? reportedFingerprint = string.IsNullOrEmpty(report.CaFingerprint)
            ? stored?.ReportedCaFingerprint
            : report.CaFingerprint;
        string? kubeletVersion = string.IsNullOrEmpty(report.KubeletVersion)
            ? stored?.KubeletVersion
            : report.KubeletVersion;
        bool mismatch = (reportedFingerprint != null) && (string.Equals(reportedFingerprint, cluster.CaFingerprint, StringComparison.Ordinal) == false);

        if (target == null)
        {
            KubernetesNode inserted = new()
            {
                TenantId = tenantId,
                ClusterId = clusterId,
                NodeName = nodeName,
                MachineId = machineId,
                ReportedCaFingerprint = reportedFingerprint,
                FingerprintMismatch = mismatch,
                LocalControlPlane = report.ControlPlane,
                KubeletVersion = kubeletVersion,
                LastLocalReportAt = report.ServerReceivedAt,
            };
            await _db.InsertWithInt64IdentityAsync(inserted, token: ct);
        }
        else
        {
            if ((target.MachineId != null) && (target.MachineId != machineId))
            {
                _logger.LogWarning(
                    "Kubernetes node {NodeName} in cluster {ClusterId} moved from machine {Previous} to {MachineId}",
                    nodeName, clusterId, target.MachineId, machineId);
            }

            long targetId = target.Id;
            await _db.KubernetesNodes
                .Where(n => n.Id == targetId)
                .Set(n => n.MachineId, (long?)machineId)
                .Set(n => n.KubeletVersion, kubeletVersion)
                .Set(n => n.LocalControlPlane, report.ControlPlane)
                .Set(n => n.LastLocalReportAt, (DateTimeOffset?)report.ServerReceivedAt)
                .Set(n => n.ReportedCaFingerprint, reportedFingerprint)
                .Set(n => n.FingerprintMismatch, mismatch)
                .UpdateAsync(ct);
        }

        if (report.ServerReceivedAt > cluster.LastSeenAt)
        {
            await _db.KubernetesClusters
                .Where(c => c.Id == clusterId)
                .Set(c => c.LastSeenAt, report.ServerReceivedAt)
                .UpdateAsync(ct);
        }

        // A distro the report could name replaces the stored one, so a cluster first seen by a host with
        // an unrecognised layout learns its distro later; a report that could not name it changes nothing.
        string distro = report.Distro;
        bool distroKnown = (string.IsNullOrEmpty(distro) == false) && string.Equals(distro, UnknownKubernetesDistro, StringComparison.Ordinal) == false;
        if (distroKnown && (string.Equals(distro, cluster.Distro, StringComparison.Ordinal) == false))
        {
            await _db.KubernetesClusters
                .Where(c => c.Id == clusterId)
                .Set(c => c.Distro, distro)
                .UpdateAsync(ct);
        }

        if (report.ReporterEnrolled)
        {
            await UpsertCandidateAsync(report, clusterId, candidate, ct);
        }
        else if (candidate != null)
        {
            await DeleteCandidateAsync(candidate, ct);
        }

        await ReKeyClusterIfRotationFinishedAsync(cluster, report.ServerReceivedAt, ct);

        return outcome;
    }

    /// <summary>
    /// Applies the report of a host that runs no kubelet. Such a host is never a node, but an enrolled
    /// one can still be a reporter candidate, as a k0s controller is. It is placed in the cluster it is
    /// already a candidate of, or else in the one cluster that holds its CA fingerprint, and never
    /// founds a cluster. A host that reports it is not enrolled has no candidate row left.
    /// </summary>
    private async Task<LocalReportOutcome> ApplyReportOfHostWithoutKubeletAsync(
        LocalNodeReport report, KubernetesReporterCandidate? candidate, CancellationToken ct)
    {
        if (report.ReporterEnrolled == false)
        {
            if (candidate != null)
            {
                await DeleteCandidateAsync(candidate, ct);
            }

            return LocalReportOutcome.NotANode;
        }

        long? clusterId = candidate?.ClusterId;
        if ((clusterId == null) && (string.IsNullOrEmpty(report.CaFingerprint) == false))
        {
            int tenantId = report.TenantId;
            string fingerprint = report.CaFingerprint;
            List<long> holders = await _db.KubernetesClusters
                .Where(c => (c.TenantId == tenantId) && (c.CaFingerprint == fingerprint))
                .OrderBy(c => c.Id)
                .Select(c => c.Id)
                .Take(2)
                .ToListAsync(ct);

            if (holders.Count == 1)
            {
                clusterId = holders[0];
            }
        }

        if (clusterId == null)
        {
            return LocalReportOutcome.NotANode;
        }

        await UpsertCandidateAsync(report, clusterId.Value, candidate, ct);

        return LocalReportOutcome.CandidateOnly;
    }

    /// <summary>
    /// Finds the node the cluster reporter lists for this machine, for a report whose node name is only
    /// the hostname: the one node of the cluster that the reporter has seen and whose
    /// <c>/etc/machine-id</c> equals the machine's system id, compared ordinally without regard to
    /// case. Returns nothing when the system id is empty, when no node or several match, or when
    /// another live machine of the tenant carries the same system id, because cloned machines share
    /// one and would otherwise take the row from each other on every report.
    /// </summary>
    private async Task<KubernetesNode?> FindReporterRowForHostnameFallbackAsync(Machine machine, long clusterId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(machine.SystemId))
        {
            return null;
        }

        int tenantId = machine.TenantId;
        string systemId = machine.SystemId.ToLowerInvariant();
        List<KubernetesNode> matches = await _db.KubernetesNodes
            .Where(n => (n.ClusterId == clusterId)
                && (n.LastReporterSeenAt != null)
                && (n.NodeMachineId != null)
                && (n.NodeMachineId.ToLower() == systemId))
            .OrderBy(n => n.Id)
            .Take(2)
            .ToListAsync(ct);

        if (matches.Count != 1)
        {
            return null;
        }

        int machinesWithSystemId = await _db.Machines
            .CountAsync(m => (m.TenantId == tenantId) && (m.IsDeleted == false) && (m.SystemId.ToLower() == systemId), ct);

        return (machinesWithSystemId == 1) ? matches[0] : null;
    }

    /// <summary>
    /// Records that the machine is a reporter candidate of a cluster, with the error its own reporter
    /// last reported. A machine is a candidate of one cluster only, so an existing row that belongs to
    /// another cluster moves to this one.
    /// </summary>
    private async Task UpsertCandidateAsync(
        LocalNodeReport report, long clusterId, KubernetesReporterCandidate? existing, CancellationToken ct)
    {
        if (existing == null)
        {
            await _db.InsertAsync(new KubernetesReporterCandidate
            {
                TenantId = report.TenantId,
                ClusterId = clusterId,
                MachineId = report.MachineId,
                ReporterError = report.ReporterError,
                LastReportAt = report.ServerReceivedAt,
            }, token: ct);

            return;
        }

        int tenantId = report.TenantId;
        long machineId = report.MachineId;
        await _db.KubernetesReporterCandidates
            .Where(c => (c.TenantId == tenantId) && (c.MachineId == machineId))
            .Set(c => c.ClusterId, clusterId)
            .Set(c => c.ReporterError, report.ReporterError)
            .Set(c => c.LastReportAt, report.ServerReceivedAt)
            .UpdateAsync(ct);
    }

    private async Task DeleteCandidateAsync(KubernetesReporterCandidate candidate, CancellationToken ct)
    {
        long clusterId = candidate.ClusterId;
        long machineId = candidate.MachineId;
        await _db.KubernetesReporterCandidates
            .Where(c => (c.ClusterId == clusterId) && (c.MachineId == machineId))
            .DeleteAsync(ct);
    }

    /// <summary>
    /// Finds the cluster for a machine that is not yet a node: the single cluster holding its CA
    /// fingerprint, the one of several sharing it that already has a node of this name, the single
    /// cluster where most of the recently reporting nodes have reported the fingerprint (a rotation or
    /// reinstall in progress), or a new cluster. Returns no cluster, with the reason, when the report
    /// cannot be placed.
    /// </summary>
    private async Task<(KubernetesCluster? Cluster, LocalReportOutcome Outcome)> ResolveClusterForUnlinkedMachineAsync(
        LocalNodeReport report, CancellationToken ct)
    {
        string? fingerprint = report.CaFingerprint;
        if (string.IsNullOrEmpty(fingerprint))
        {
            return (null, LocalReportOutcome.NoFingerprint);
        }

        int tenantId = report.TenantId;
        string nodeName = report.NodeName;
        List<KubernetesCluster> matches = await _db.KubernetesClusters
            .Where(c => (c.TenantId == tenantId) && (c.CaFingerprint == fingerprint))
            .OrderBy(c => c.Id)
            .ToListAsync(ct);

        if (matches.Count == 1)
        {
            return (matches[0], LocalReportOutcome.Grouped);
        }

        if (matches.Count > 1)
        {
            // The CA no longer tells the clusters apart, so the node name is the only evidence left.
            List<long> matchIds = matches.Select(c => c.Id).ToList();
            List<long> named = await _db.KubernetesNodes
                .Where(n => (n.TenantId == tenantId) && (n.NodeName == nodeName) && matchIds.Contains(n.ClusterId))
                .Select(n => n.ClusterId)
                .Distinct()
                .ToListAsync(ct);

            if (named.Count == 1)
            {
                long namedId = named[0];

                return (matches.First(c => c.Id == namedId), LocalReportOutcome.Grouped);
            }

            return (null, LocalReportOutcome.Ambiguous);
        }

        // No cluster holds the fingerprint. If exactly one cluster has most of its recently reporting
        // nodes on it, this is a rotation or reinstall in progress and the machine belongs there. A
        // single node that drifted ahead of the others is not enough, so it cannot pull a newcomer into
        // its cluster.
        DateTimeOffset recentSince = report.ServerReceivedAt - KubernetesRecentReportWindow;
        List<long> candidates = await _db.KubernetesNodes
            .Where(n => (n.TenantId == tenantId) && (n.ReportedCaFingerprint == fingerprint) && (n.LastLocalReportAt >= recentSince))
            .Select(n => n.ClusterId)
            .Distinct()
            .ToListAsync(ct);

        List<long> joinable = [];
        if (candidates.Count > 0)
        {
            List<KubernetesNode> recentNodes = await _db.KubernetesNodes
                .Where(n => (n.TenantId == tenantId) && candidates.Contains(n.ClusterId) && (n.LastLocalReportAt >= recentSince))
                .ToListAsync(ct);

            foreach (long candidateId in candidates)
            {
                int recent = recentNodes.Count(n => n.ClusterId == candidateId);
                int agreeing = recentNodes.Count(n => (n.ClusterId == candidateId) && string.Equals(n.ReportedCaFingerprint, fingerprint, StringComparison.Ordinal));
                if ((agreeing * 2) > recent)
                {
                    joinable.Add(candidateId);
                }
            }
        }

        if (joinable.Count == 1)
        {
            long joinableId = joinable[0];
            KubernetesCluster joined = await _db.KubernetesClusters.FirstAsync(c => c.Id == joinableId, ct);

            return (joined, LocalReportOutcome.Grouped);
        }

        KubernetesCluster created = new()
        {
            TenantId = tenantId,
            CaFingerprint = fingerprint,
            Distro = report.Distro,
            FirstSeenAt = report.ServerReceivedAt,
            LastSeenAt = report.ServerReceivedAt,
        };
        created.Id = await _db.InsertWithInt64IdentityAsync(created, token: ct);

        return (created, LocalReportOutcome.ClusterCreated);
    }

    /// <summary>
    /// Returns the cluster a machine has moved to: the one other cluster in the tenant that holds the
    /// fingerprint the machine reports now. A machine moves only when that is unambiguous: with no
    /// fingerprint, a fingerprint its own cluster holds, or no other holder (as during a rotation, when
    /// nobody holds the new CA yet) or several, it stays where it is.
    /// </summary>
    private async Task<KubernetesCluster?> FindClusterTheMachineMovedToAsync(
        KubernetesCluster current, string? fingerprint, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fingerprint) || string.Equals(fingerprint, current.CaFingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        long currentId = current.Id;
        int tenantId = current.TenantId;
        List<KubernetesCluster> holders = await _db.KubernetesClusters
            .Where(c => (c.TenantId == tenantId) && (c.Id != currentId) && (c.CaFingerprint == fingerprint))
            .OrderBy(c => c.Id)
            .Take(2)
            .ToListAsync(ct);

        return holders.Count == 1 ? holders[0] : null;
    }

    /// <summary>
    /// Re-keys a cluster to a new CA fingerprint once a rotation or reinstall has finished: every node
    /// that reported within the hour agrees on one fingerprint that differs from the cluster's, and no
    /// other cluster in the tenant already holds it. Every node's mismatch flag is then recomputed
    /// against the new key.
    /// </summary>
    private async Task ReKeyClusterIfRotationFinishedAsync(KubernetesCluster cluster, DateTimeOffset serverReceivedAt, CancellationToken ct)
    {
        long clusterId = cluster.Id;
        int tenantId = cluster.TenantId;
        List<KubernetesNode> nodes = await _db.KubernetesNodes.Where(n => n.ClusterId == clusterId).ToListAsync(ct);

        DateTimeOffset recentSince = serverReceivedAt - KubernetesRecentReportWindow;
        List<string> recentFingerprints = nodes
            .Where(n => (n.ReportedCaFingerprint != null) && (n.LastLocalReportAt >= recentSince))
            .Select(n => n.ReportedCaFingerprint!)
            .Distinct()
            .ToList();

        if (recentFingerprints.Count != 1)
        {
            return;
        }

        string newFingerprint = recentFingerprints[0];
        if (string.Equals(newFingerprint, cluster.CaFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        bool heldElsewhere = await _db.KubernetesClusters
            .AnyAsync(c => (c.TenantId == tenantId) && (c.Id != clusterId) && (c.CaFingerprint == newFingerprint), ct);

        if (heldElsewhere)
        {
            return;
        }

        await _db.KubernetesClusters
            .Where(c => c.Id == clusterId)
            .Set(c => c.CaFingerprint, newFingerprint)
            .UpdateAsync(ct);

        _logger.LogInformation(
            "Kubernetes cluster {ClusterId} re-keyed from CA fingerprint {PreviousFingerprint} to {Fingerprint}",
            clusterId, cluster.CaFingerprint, newFingerprint);

        foreach (KubernetesNode node in nodes)
        {
            bool mismatch = (node.ReportedCaFingerprint != null) && (string.Equals(node.ReportedCaFingerprint, newFingerprint, StringComparison.Ordinal) == false);
            if (mismatch == node.FingerprintMismatch)
            {
                continue;
            }

            long nodeId = node.Id;
            await _db.KubernetesNodes
                .Where(n => n.Id == nodeId)
                .Set(n => n.FingerprintMismatch, mismatch)
                .UpdateAsync(ct);
        }
    }

    /// <summary>
    /// Records the projection cursor, never moving it backwards: a replica that lost the projection
    /// lock mid-batch can still finish a row with a lower id, and must not rewind its successor's
    /// progress. The timestamp changes only when the position does.
    /// </summary>
    private async Task UpsertKubernetesCursorAsync(long position, DateTimeOffset now, CancellationToken ct)
    {
        // Atomic upsert so two replicas advancing the cursor of an empty table cannot race on a
        // primary-key violation.
        if (_db.DataProvider.Name.Contains("PostgreSQL"))
        {
            await _db.ExecuteAsync(
                """
                INSERT INTO "KubernetesInventoryProjectionCursor" ("Id", "Position", "UpdatedAt")
                VALUES (@id, @position, @updatedAt)
                ON CONFLICT ("Id")
                DO UPDATE SET
                    "Position" = GREATEST("KubernetesInventoryProjectionCursor"."Position", EXCLUDED."Position"),
                    "UpdatedAt" = CASE
                        WHEN EXCLUDED."Position" > "KubernetesInventoryProjectionCursor"."Position" THEN EXCLUDED."UpdatedAt"
                        ELSE "KubernetesInventoryProjectionCursor"."UpdatedAt"
                    END
                """,
                ct,
                new DataParameter("@id", KubernetesCursorRowId),
                new DataParameter("@position", position),
                new DataParameter("@updatedAt", now));

            return;
        }

        // SQLite test path: a guarded update, then an insert when there was no row to update.
        int updated = await _db.KubernetesInventoryProjectionCursors
            .Where(c => (c.Id == KubernetesCursorRowId) && (c.Position < position))
            .Set(c => c.Position, position)
            .Set(c => c.UpdatedAt, now)
            .UpdateAsync(ct);

        if (updated > 0)
        {
            return;
        }

        bool exists = await _db.KubernetesInventoryProjectionCursors.AnyAsync(c => c.Id == KubernetesCursorRowId, ct);
        if (exists == false)
        {
            await _db.InsertAsync(new KubernetesInventoryProjectionCursor
            {
                Id = KubernetesCursorRowId,
                Position = position,
                UpdatedAt = now,
            }, token: ct);
        }
    }

    private static bool IsNewerThan(DateTimeOffset? stored, DateTimeOffset incoming)
    {
        return (stored != null) && (stored.Value > incoming);
    }

    /// <summary>
    /// How many values one <c>IN</c> list carries, which keeps a snapshot of any size well below the
    /// parameter limit of both database providers.
    /// </summary>
    private const int KubernetesInListChunkSize = 500;

    /// <summary>Options for the JSON text stored in the node columns that hold lists.</summary>
    private static readonly JsonSerializerOptions KubernetesStoredJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A machine's id with its system id, which is all the machine-id link needs of a machine.</summary>
    private sealed record MachineSystemId(long Id, string SystemId);

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

        List<KubernetesNode> moving = await _db.KubernetesNodes
            .Where(n => (n.ClusterId == fromId) && names.Contains(n.NodeName))
            .ToListAsync(ct);

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
    /// report, comes from the row that was updated last. The machine link comes from whichever row has
    /// one, the row with the newer local report first. The other row is deleted before the kept row takes
    /// its link, so the unique index over the link holds.
    /// </summary>
    private async Task CombineNodesAsync(KubernetesNode kept, KubernetesNode moving, CancellationToken ct)
    {
        KubernetesNode local = (Nullable.Compare(moving.LastLocalReportAt, kept.LastLocalReportAt) > 0) ? moving : kept;
        KubernetesNode otherLocal = ReferenceEquals(local, kept) ? moving : kept;
        KubernetesNode sighting = (Nullable.Compare(moving.LastReporterSeenAt, kept.LastReporterSeenAt) > 0) ? moving : kept;
        KubernetesNode freshest = (Nullable.Compare(LastActivityOf(moving), LastActivityOf(kept)) > 0) ? moving : kept;
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
            .Set(n => n.KubeletVersion, freshest.KubeletVersion)
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

        await _db.KubernetesClusters
            .Where(c => c.Id == clusterId)
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

    /// <summary>
    /// Creates or updates a row for every node the snapshot lists, then links the new and still unlinked
    /// rows to machines by machine id. Fields only a node's own machine can report are left alone, except
    /// the kubelet version, which the cluster supplies when the node has had no local report within the
    /// staleness threshold.
    /// </summary>
    private async Task UpsertSnapshotNodesAsync(long clusterId, ClusterSnapshot snapshot, CancellationToken ct)
    {
        int tenantId = snapshot.TenantId;
        DateTimeOffset receivedAt = snapshot.ServerReceivedAt;
        DateTimeOffset localReportStaleBefore = receivedAt - KubernetesReportingPolicy.StaleThreshold;
        List<ClusterSnapshotNode> listed = snapshot.Nodes.DistinctBy(n => n.Name, StringComparer.Ordinal).ToList();
        List<string> names = listed.Select(n => n.Name).ToList();

        Dictionary<string, KubernetesNode> existing = [];
        if (names.Count > 0)
        {
            List<KubernetesNode> rows = await _db.KubernetesNodes
                .Where(n => (n.ClusterId == clusterId) && names.Contains(n.NodeName))
                .ToListAsync(ct);
            existing = rows.ToDictionary(n => n.NodeName, StringComparer.Ordinal);
        }

        List<(KubernetesNode Row, ClusterSnapshotNode Listed)> unlinked = [];
        foreach (ClusterSnapshotNode node in listed)
        {
            string? nodeMachineId = string.IsNullOrEmpty(node.MachineId) ? null : node.MachineId;
            string roles = JsonSerializer.Serialize(node.Roles, KubernetesStoredJsonOptions);
            string conditions = JsonSerializer.Serialize(node.Conditions, KubernetesStoredJsonOptions);
            int? podCapacity = node.PodCapacity;

            if (existing.TryGetValue(node.Name, out KubernetesNode? row) == false)
            {
                KubernetesNode inserted = new()
                {
                    TenantId = tenantId,
                    ClusterId = clusterId,
                    NodeName = node.Name,
                    KubeletVersion = node.KubeletVersion,
                    NodeMachineId = nodeMachineId,
                    Roles = roles,
                    ReadyStatus = node.ReadyStatus,
                    ReadySince = node.ReadySince,
                    Conditions = conditions,
                    Unschedulable = node.Unschedulable,
                    UnschedulableSince = node.Unschedulable ? receivedAt : null,
                    ContainerRuntimeVersion = node.ContainerRuntimeVersion,
                    PodCount = node.PodCount,
                    PodCapacity = podCapacity,
                    LastReporterSeenAt = receivedAt,
                };
                inserted.Id = await _db.InsertWithInt64IdentityAsync(inserted, token: ct);
                unlinked.Add((inserted, node));

                continue;
            }

            // A node's own machine knows its kubelet version best, so the cluster's value only fills in
            // when that machine has been silent for the whole threshold.
            bool hasRecentLocalReport = (row.LastLocalReportAt != null) && (row.LastLocalReportAt.Value >= localReportStaleBefore);
            string? kubeletVersion = (hasRecentLocalReport || (node.KubeletVersion == null)) ? row.KubeletVersion : node.KubeletVersion;

            // Kubernetes does not record when a node was cordoned, so the first snapshot that shows it
            // unschedulable dates it, and a snapshot that shows it schedulable clears the date.
            DateTimeOffset? unschedulableSince = node.Unschedulable ? (row.UnschedulableSince ?? receivedAt) : null;
            long rowId = row.Id;
            bool unschedulable = node.Unschedulable;
            string? readyStatus = node.ReadyStatus;
            DateTimeOffset? readySince = node.ReadySince;
            string? containerRuntimeVersion = node.ContainerRuntimeVersion;
            int podCount = node.PodCount;

            await _db.KubernetesNodes
                .Where(n => n.Id == rowId)
                .Set(n => n.NodeMachineId, nodeMachineId)
                .Set(n => n.Roles, roles)
                .Set(n => n.ReadyStatus, readyStatus)
                .Set(n => n.ReadySince, readySince)
                .Set(n => n.Conditions, conditions)
                .Set(n => n.Unschedulable, unschedulable)
                .Set(n => n.UnschedulableSince, unschedulableSince)
                .Set(n => n.ContainerRuntimeVersion, containerRuntimeVersion)
                .Set(n => n.PodCount, (int?)podCount)
                .Set(n => n.PodCapacity, podCapacity)
                .Set(n => n.KubeletVersion, kubeletVersion)
                .Set(n => n.LastReporterSeenAt, (DateTimeOffset?)receivedAt)
                .UpdateAsync(ct);

            if (row.MachineId == null)
            {
                unlinked.Add((row, node));
            }
        }

        await LinkNodesToMachinesAsync(tenantId, unlinked, ct);
    }

    /// <summary>
    /// Links unlinked nodes to enrolled machines by <c>/etc/machine-id</c>, only where that is
    /// unambiguous: exactly one non-deleted machine of the tenant has a non-empty system id equal to the
    /// node's machine id (ordinal, ignoring case), the machine is not already linked to another node, and
    /// the snapshot lists no other node with the same machine id, which is what clones of one template
    /// look like.
    /// </summary>
    private async Task LinkNodesToMachinesAsync(
        int tenantId, List<(KubernetesNode Row, ClusterSnapshotNode Listed)> unlinked, CancellationToken ct)
    {
        List<(KubernetesNode Row, string Key)> candidates = unlinked
            .Where(u => string.IsNullOrWhiteSpace(u.Listed.MachineId) == false)
            .Select(u => (u.Row, u.Listed.MachineId!.ToLowerInvariant()))
            .ToList();

        HashSet<string> shared = candidates
            .GroupBy(c => c.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        candidates = candidates.Where(c => shared.Contains(c.Key) == false).ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        List<string> keys = candidates.Select(c => c.Key).Distinct(StringComparer.Ordinal).ToList();
        List<MachineSystemId> machines = [];
        foreach (string[] chunk in keys.Chunk(KubernetesInListChunkSize))
        {
            List<string> chunkKeys = chunk.ToList();
            machines.AddRange(await _db.Machines
                .Where(m => (m.TenantId == tenantId) && (m.IsDeleted == false) && chunkKeys.Contains(m.SystemId.ToLower()))
                .Select(m => new MachineSystemId(m.Id, m.SystemId))
                .ToListAsync(ct));
        }

        Dictionary<string, long> uniqueMachines = machines
            .GroupBy(m => m.SystemId.ToLowerInvariant(), StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        HashSet<long> linkedMachines = [];
        foreach (long[] chunk in uniqueMachines.Values.Chunk(KubernetesInListChunkSize))
        {
            List<long> chunkIds = chunk.ToList();
            List<long?> linked = await _db.KubernetesNodes
                .Where(n => (n.MachineId != null) && chunkIds.Contains(n.MachineId.Value))
                .Select(n => n.MachineId)
                .ToListAsync(ct);
            linkedMachines.UnionWith(linked.Where(id => id != null).Select(id => id!.Value));
        }

        foreach ((KubernetesNode row, string key) in candidates)
        {
            if ((uniqueMachines.TryGetValue(key, out long machineId) == false) || linkedMachines.Contains(machineId))
            {
                continue;
            }

            long rowId = row.Id;
            await _db.KubernetesNodes
                .Where(n => n.Id == rowId)
                .Set(n => n.MachineId, (long?)machineId)
                .UpdateAsync(ct);
            linkedMachines.Add(machineId);
        }
    }

    /// <summary>
    /// Deletes the cluster's nodes that a complete snapshot no longer lists, unless their own machine
    /// has reported within the staleness threshold, which can mean the cluster reporter is the one that
    /// is wrong or lagging.
    /// </summary>
    private async Task RemoveNodesAbsentFromSnapshotAsync(long clusterId, ClusterSnapshot snapshot, CancellationToken ct)
    {
        DateTimeOffset localReportStaleBefore = snapshot.ServerReceivedAt - KubernetesReportingPolicy.StaleThreshold;
        List<string> names = snapshot.Nodes.Select(n => n.Name).Distinct(StringComparer.Ordinal).ToList();

        IQueryable<KubernetesNode> absent = _db.KubernetesNodes
            .Where(n => (n.ClusterId == clusterId)
                && ((n.LastLocalReportAt == null) || (n.LastLocalReportAt < localReportStaleBefore)));

        if (names.Count > 0)
        {
            absent = absent.Where(n => names.Contains(n.NodeName) == false);
        }

        await absent.DeleteAsync(ct);
    }
}
