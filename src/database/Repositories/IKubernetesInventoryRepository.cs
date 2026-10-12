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
    /// machine into a cluster by established membership or CA fingerprint, links or updates its node,
    /// re-keys the cluster when a CA rotation has finished, and advances the projection cursor to the
    /// report's telemetry id in the same transaction whatever the outcome. The method opens that
    /// transaction itself, so it must not be called inside one: a transaction already open on the same
    /// connection would make the nested begin fail.
    /// </summary>
    /// <param name="report">The report to apply.</param>
    /// <param name="now">The time to record as the cursor's last advance.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>What the report did to the inventory.</returns>
    Task<LocalReportOutcome> ApplyLocalNodeReportAsync(LocalNodeReport report, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Deletes nodes that no local report has described for seven days and then the clusters left
    /// with no nodes, one tenant at a time, each in its own transaction under that tenant's lock. The
    /// method opens those transactions itself, so it must not be called inside one.
    /// </summary>
    /// <param name="now">The time staleness is measured from.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The total number of rows deleted.</returns>
    Task<int> CollectGarbageAsync(DateTimeOffset now, CancellationToken ct);

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
