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
    /// <summary>
    /// First key of the Kubernetes inventory advisory lock, keeping it distinct from every other
    /// advisory lock taken against this database.
    /// </summary>
    private const int KubernetesTenantLockNamespace = 0x4B38_5349;

    /// <summary>The projection is a single stream, so its cursor table holds one row with this key.</summary>
    private const int KubernetesCursorRowId = 1;

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
}
