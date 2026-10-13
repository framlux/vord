// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using Microsoft.Extensions.Logging;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IKubernetesInventoryRepository
{
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
    /// reporter described, and nodes no machine is linked to, are deleted, the other nodes lose their
    /// reporter columns, and the cluster loses its reporter columns and its Lease epoch, because
    /// enabling reporting again creates a new Lease. A cluster that is still receiving snapshots is
    /// never touched, however far its candidates' local reports lag. Must run inside the tenant's
    /// transaction and lock.
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

        // A node no machine is linked to is dead even when a local report once described it: a live
        // report re-links any existing row for its machine, so an unlinked row belongs to a machine that
        // moved to another cluster, was renamed or was deleted, and its source now reports into another row.
        int deadNodes = await _db.KubernetesNodes
            .Where(n => disabled.Contains(n.ClusterId) && ((n.LastLocalReportAt == null) || (n.MachineId == null)))
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

        return deadNodes;
    }
}
