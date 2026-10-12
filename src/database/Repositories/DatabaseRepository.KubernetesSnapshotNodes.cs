// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using System.Text.Json;

namespace Framlux.FleetManagement.Database.Repositories;

/// <inheritdoc/>
public partial class DatabaseRepository : IKubernetesInventoryRepository
{
    /// <summary>A machine's id with its system id, which is all the machine-id link needs of a machine.</summary>
    private sealed record MachineSystemId(long Id, string SystemId);

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
