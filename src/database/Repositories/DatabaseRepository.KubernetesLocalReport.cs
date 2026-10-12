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

    private static bool IsNewerThan(DateTimeOffset? stored, DateTimeOffset incoming)
    {
        return (stored != null) && (stored.Value > incoming);
    }
}
