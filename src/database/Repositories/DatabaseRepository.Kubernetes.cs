// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
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

    /// <summary>
    /// The rows the Kubernetes projector may read past its cursor, written once so the batch read and
    /// the lag measurement can never disagree about which rows count. The telemetry type filter is a
    /// SQL literal rather than a parameter so the planner can prove it implies the predicate of the
    /// partial index over the Kubernetes types; a parameter would keep that index out of reach. Only
    /// rows that have been visible for the safety lag are read (ServerReceivedAt &lt;= visibleBefore),
    /// so a lower-id row that committed after the cursor passed it is still picked up instead of being
    /// skipped forever.
    /// </summary>
    private const string KubernetesProjectorReadableRows =
        """
        WHERE "TelemetryType" IN (14, 15) AND "Id" > @afterId AND "ReceivedAt" > @receivedAfter
          AND "ServerReceivedAt" <= @visibleBefore
        """;

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
        List<MachineTelemetry> rows = await _db.QueryToListAsync<MachineTelemetry>(
            $"""
            SELECT * FROM "MachineTelemetry"
            {KubernetesProjectorReadableRows}
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
    public async Task<DateTimeOffset?> GetOldestUnprojectedKubernetesReceiptAsync(
        long afterId, DateTimeOffset receivedAfter, DateTimeOffset visibleBefore, CancellationToken ct)
    {
        // The first readable row by id, exactly as the batch read takes it, and never MIN of the
        // receipt time: the minimum would have to visit every unprojected row, so the query would cost
        // the most during the backlog it exists to report. Ids ascend with insert, so the first row by
        // id is the one the projector applies next.
        List<DateTimeOffset> receipts = await _db.QueryToListAsync<DateTimeOffset>(
            $"""
            SELECT "ServerReceivedAt" FROM "MachineTelemetry"
            {KubernetesProjectorReadableRows}
            ORDER BY "Id" LIMIT 1
            """,
            ct,
            new DataParameter("@afterId", afterId),
            new DataParameter("@receivedAfter", receivedAfter),
            new DataParameter("@visibleBefore", visibleBefore));

        return receipts.Count == 0 ? null : receipts[0];
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
        int tenantId, long clusterId, CancellationToken ct)
    {
        IQueryable<long> nodeMachines = _db.KubernetesNodes
            .Where(n => (n.TenantId == tenantId) && (n.ClusterId != clusterId) && (n.MachineId != null))
            .Select(n => n.MachineId!.Value);
        IQueryable<long> candidateMachines = _db.KubernetesReporterCandidates
            .Where(c => (c.TenantId == tenantId) && (c.ClusterId != clusterId))
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
            // One statement for every machine asked about, however many. Each machine is looked up on its
            // own: its rows newest first by server receipt time, with the row id breaking a tie, stopping
            // at the first. Read through the machine, type and server-time index, that costs about one row
            // per partition plus the rows tied with the newest, where grouping the whole window would read
            // and sort every report each machine sent in it. The type is a SQL literal for the same reason
            // as in the projector's batch read.
            rows = await _db.QueryToListAsync<MachineTelemetry>(
                """
                SELECT latest."MachineId", latest."Payload"
                FROM unnest(@machineIds) AS asked("MachineId")
                CROSS JOIN LATERAL (
                    SELECT t."MachineId", t."Payload" FROM "MachineTelemetry" t
                    WHERE t."TenantId" = @tenantId AND t."TelemetryType" = 14 AND t."MachineId" = asked."MachineId"
                      AND t."ServerReceivedAt" > @serverReceivedAfter AND t."ReceivedAt" > @partitionLowerBound
                    ORDER BY t."ServerReceivedAt" DESC, t."Id" DESC
                    LIMIT 1
                ) AS latest
                """,
                ct,
                new DataParameter("@tenantId", tenantId),
                new DataParameter("@machineIds", ids),
                new DataParameter("@serverReceivedAfter", serverReceivedAfter),
                new DataParameter("@partitionLowerBound", partitionLowerBound));
        }
        else
        {
            // SQLite test path, which has no LATERAL join: the same newest-row-per-machine rule as a group-by.
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
}
