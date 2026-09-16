// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database.Models;

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for machine state summary and detail operations.
/// </summary>
public interface IMachineStateRepository
{
    /// <summary>
    /// Inserts a new machine state summary row. Used during machine registration.
    /// </summary>
    /// <param name="summary">The summary row to insert.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task InsertSummaryAsync(MachineStateSummary summary, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new machine state detail row. Used during machine registration.
    /// </summary>
    /// <param name="detail">The detail row to insert.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task InsertDetailAsync(MachineStateDetail detail, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk-inserts telemetry rows using the most efficient copy strategy.
    /// </summary>
    /// <param name="rows">The telemetry rows to insert.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task BulkInsertTelemetryAsync(List<MachineTelemetry> rows, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a single telemetry row.
    /// </summary>
    /// <param name="row">The telemetry row to insert.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task InsertTelemetryAsync(MachineTelemetry row, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves one day's worth of a tenant's telemetry into the given retention class. Because
    /// <c>RetentionClass</c> is the LIST partition key of <c>MachineTelemetry</c>, this UPDATE is a
    /// keyed row movement on PostgreSQL: the rows physically relocate to the target class's daily leaf
    /// partition and thereafter expire on that class's schedule. The caller chunks by day so each
    /// statement stays small and lock-friendly.
    /// </summary>
    /// <param name="tenantId">The tenant whose rows move. Rows of other tenants are never touched.</param>
    /// <param name="target">The retention class the rows move into.</param>
    /// <param name="dayStart">Inclusive lower bound on <see cref="MachineTelemetry.ReceivedAt"/>.</param>
    /// <param name="dayEnd">Exclusive upper bound on <see cref="MachineTelemetry.ReceivedAt"/>.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The number of rows moved. Rows already in the target class are not counted.</returns>
    Task<int> ReclassifyTelemetryForTenantAsync(
        int tenantId,
        RetentionClass target,
        DateTimeOffset dayStart,
        DateTimeOffset dayEnd,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the distinct tenant IDs that have at least one machine state summary row.
    /// Used by the health sweep service to partition work by tenant.
    /// </summary>
    Task<List<int>> GetDistinctTenantIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a raw SQL health sweep for a single tenant.
    /// The caller provides the dialect-specific SQL string.
    /// </summary>
    /// <param name="sql">Dialect-specific SQL for the health sweep.</param>
    /// <param name="tenantId">The tenant to sweep.</param>
    /// <param name="onlineThresholdSeconds">Seconds before a machine is considered offline.</param>
    /// <param name="staleSeconds">Seconds of telemetry silence after which an online machine's telemetry counts as stale.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of rows affected.</returns>
    Task<int> SweepHealthStatusAsync(string sql, int tenantId, int onlineThresholdSeconds, int staleSeconds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records an agent heartbeat against the machine's summary row, never moving the recorded
    /// time backward.
    /// </summary>
    /// <param name="machineId">The machine that sent the heartbeat.</param>
    /// <param name="receivedAt">Server receipt time of the heartbeat. Never the agent clock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecordHeartbeatAsync(long machineId, DateTimeOffset receivedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the state summary for a single machine.
    /// </summary>
    Task<MachineStateSummary?> GetSummaryForMachineAsync(long machineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all state summaries for machines belonging to a tenant that are not deleted.
    /// Used by the alert evaluation service.
    /// </summary>
    Task<List<MachineStateSummary>> GetSummariesForTenantMachinesAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a dictionary mapping machine IDs to their hostnames from state summaries.
    /// </summary>
    Task<Dictionary<long, string?>> GetHostnameMapAsync(List<long> machineIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns state summaries for the specified machine IDs as a list.
    /// </summary>
    Task<List<MachineStateSummary>> GetSummaryListByMachineIdsAsync(List<long> machineIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the Name column on the machine state summary for a given machine, scoped to the owning tenant.
    /// </summary>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="tenantId">The tenant that must own the summary for the update to apply.</param>
    /// <param name="name">The new name.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><c>true</c> if a matching summary in the tenant was updated; otherwise <c>false</c>.</returns>
    Task<bool> UpdateSummaryNameAsync(long machineId, int tenantId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a combined machine state summary patch as a single UPDATE. Sets only the columns
    /// owned by the telemetry types present in the patch and advances LastSeenAt with a monotonic
    /// guard so an already-stored value is never moved backward.
    /// </summary>
    /// <param name="patch">The combined summary patch produced from a collapsed telemetry batch.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task ApplySummaryPatchAsync(MachineSummaryPatch patch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a combined machine state detail patch as a single UPDATE. Sets only the columns
    /// owned by the telemetry types present in the patch. Issues no update when the patch carries
    /// no detail-bearing types.
    /// </summary>
    /// <param name="patch">The combined detail patch produced from a collapsed telemetry batch.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task ApplyDetailPatchAsync(MachineDetailPatch patch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the next batch of telemetry rows past the high-water mark within the streaming
    /// window, restricted to the machines owned by the given shard under modulo partitioning.
    /// Only rows whose server receipt time is at or before <paramref name="visibilityCutoff"/> are
    /// returned, giving out-of-order commits time to become visible before the cursor passes them.
    /// </summary>
    Task<List<MachineTelemetry>> GetTelemetryBatchAsync(long highWaterMark, DateTimeOffset streamingWindow, DateTimeOffset visibilityCutoff, int batchSize, int shardIndex, int shardCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a single page of telemetry rows for the specified machine IDs and telemetry type,
    /// bounded to rows received at or after <paramref name="receivedSince"/>, ordered by ReceivedAt
    /// descending. Pagination (Skip/Take) is performed in SQL so the full history is never loaded
    /// into memory.
    /// </summary>
    /// <param name="machineIds">The machine IDs to filter by.</param>
    /// <param name="telemetryType">The telemetry type identifier.</param>
    /// <param name="receivedSince">The inclusive lower bound on the row received timestamp.</param>
    /// <param name="skip">The number of rows to skip.</param>
    /// <param name="take">The maximum number of rows to return.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<MachineTelemetry>> GetTelemetryPageByMachineIdsAndTypeAsync(
        List<long> machineIds, short telemetryType, DateTimeOffset receivedSince, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the total count of telemetry rows for the specified machine IDs and telemetry type
    /// that were received at or after <paramref name="receivedSince"/>. Used to compute total page
    /// count without materializing rows.
    /// </summary>
    /// <param name="machineIds">The machine IDs to filter by.</param>
    /// <param name="telemetryType">The telemetry type identifier.</param>
    /// <param name="receivedSince">The inclusive lower bound on the row received timestamp.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<int> CountTelemetryByMachineIdsAndTypeAsync(
        List<long> machineIds, short telemetryType, DateTimeOffset receivedSince, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a cursor-based batch of telemetry rows for the specified machines, ordered by ID ascending.
    /// Used for data export operations.
    /// </summary>
    /// <param name="machineIds">The machine IDs to filter by.</param>
    /// <param name="afterId">Return only rows with ID greater than this value.</param>
    /// <param name="batchSize">Maximum number of rows to return.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<MachineTelemetry>> GetTelemetryExportBatchAsync(List<long> machineIds, long afterId, int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the latest telemetry row for each telemetry type for a machine, looking back a specified number of days.
    /// Used for machine detail views.
    /// </summary>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="daysBack">Number of days to look back from now.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<Dictionary<short, MachineTelemetry>> GetLatestTelemetryPerTypeAsync(long machineId, int daysBack, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns recent telemetry rows of a specific type for a machine, ordered by most recent first.
    /// Used for SSH session history and similar views.
    /// </summary>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="telemetryType">The telemetry type identifier.</param>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<MachineTelemetry>> GetRecentTelemetryAsync(long machineId, short telemetryType, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns telemetry rows for a machine filtered by type and time range, ordered by ReceivedAt ascending.
    /// Used for historical trend charts. The time range filter enables Postgres partition pruning.
    /// </summary>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="telemetryType">The telemetry type identifier.</param>
    /// <param name="rangeStart">Inclusive start of the time range.</param>
    /// <param name="rangeEnd">Exclusive end of the time range.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<MachineTelemetry>> GetTelemetryHistoryAsync(long machineId, short telemetryType, DateTimeOffset rangeStart, DateTimeOffset rangeEnd, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts telemetry rows of one type for a machine whose payload contains a marker substring and
    /// whose server receipt time falls inside an inclusive window. The count is computed by the
    /// database; no payload ever crosses the wire.
    /// </summary>
    /// <remarks>
    /// The window is expressed in <see cref="MachineTelemetry.ServerReceivedAt"/> because that is the
    /// only recency signal in the row — <see cref="MachineTelemetry.ReceivedAt"/> is derived from the
    /// agent's clock and a skewed agent could otherwise place an attack outside the window or
    /// manufacture one inside it. A generous <c>ReceivedAt</c> lower bound is added by the
    /// implementation purely so PostgreSQL can still prune partitions, which are ranged on that
    /// column; it is wide enough that it can never exclude a row the server-time predicate keeps.
    /// <para>
    /// The marker is matched as a substring of the stored JSON rather than by parsing it, so this
    /// stays a single aggregate query under a brute-force rate. Callers own the marker string and
    /// must pin the serialized payload shape with a test, since a serializer change would silently
    /// return zero rather than fail.
    /// </para>
    /// </remarks>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="telemetryType">The telemetry type identifier.</param>
    /// <param name="payloadMarker">Substring the payload must contain.</param>
    /// <param name="windowStart">Inclusive start of the server-receipt window.</param>
    /// <param name="windowEnd">Inclusive end of the server-receipt window.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<int> CountTelemetryWithPayloadMarkerAsync(long machineId, short telemetryType, string payloadMarker, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the payloads of the most recent telemetry rows matching the same filters as
    /// <see cref="CountTelemetryWithPayloadMarkerAsync"/>, newest first and capped at
    /// <paramref name="limit"/> rows. Only the payload column is selected.
    /// </summary>
    /// <remarks>
    /// This exists for describing an incident after it has already been decided, never for deciding
    /// one: the decision is the count, which the database computes. A caller that reaches for this
    /// on every evaluation has turned an aggregate into a row haul.
    /// </remarks>
    /// <param name="machineId">The machine ID.</param>
    /// <param name="telemetryType">The telemetry type identifier.</param>
    /// <param name="payloadMarker">Substring the payload must contain.</param>
    /// <param name="windowStart">Inclusive start of the server-receipt window.</param>
    /// <param name="windowEnd">Inclusive end of the server-receipt window.</param>
    /// <param name="limit">Maximum number of payloads to return.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<List<string>> GetTelemetryPayloadsWithMarkerAsync(long machineId, short telemetryType, string payloadMarker, DateTimeOffset windowStart, DateTimeOffset windowEnd, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns health count aggregation (grouped by HealthStatus) and total security updates
    /// for all machines in a tenant, using a Machines LEFT JOIN MachineStateSummaries query.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<(List<(short HealthStatus, int Count)> HealthCounts, int TotalSecurityUpdates)> GetFleetHealthAggregationAsync(int tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches fleet machines with comprehensive SQL-level filtering, sorting, and pagination.
    /// Joins Machines with MachineStateSummaries and applies criteria-based WHERE clauses at the SQL level.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="parameters">Search parameters including filters, sort, and pagination.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<(List<FleetMachineRow> Rows, int TotalCount)> SearchFleetMachinesAsync(int tenantId, FleetSearchParameters parameters, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the ids of every machine matching the search filters, up to <paramref name="maxIds"/>,
    /// along with the unbounded match count so a caller can tell a complete answer from a capped one.
    /// Sorting and paging in <paramref name="parameters"/> are ignored: this answers "which machines
    /// does this filter match", not "which page of them".
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="parameters">Search parameters supplying the filters.</param>
    /// <param name="maxIds">The most ids to return.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<(List<long> Ids, int TotalCount)> SearchFleetMachineIdsAsync(int tenantId, FleetSearchParameters parameters, int maxIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the stored projection cursor position for the given shard, or null when no cursor
    /// row exists yet for that shard.
    /// </summary>
    /// <param name="shardIndex">The projection shard's zero-based index.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<long?> GetProjectionCursorAsync(int shardIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts machines across every tenant, grouped by health status. Deliberately fleet-wide: this
    /// answers an operator's question about the installation, not a customer's about their fleet.
    /// Keyed by the stored short rather than the health enum, which lives in a project this one
    /// cannot reference.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A count per health status; statuses with no machines are absent.</returns>
    Task<IReadOnlyDictionary<short, int>> GetFleetMachineCountsByHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the server receipt time of the oldest telemetry row this shard has not yet
    /// projected and that the projection read would actually pick up, or null when the shard is
    /// caught up.
    /// </summary>
    /// <param name="cursor">The shard's high-water mark row id.</param>
    /// <param name="streamingWindow">The oldest receipt time the projection still reads.</param>
    /// <param name="visibilityCutoff">The newest receipt time considered visible, applying the safety lag.</param>
    /// <param name="shardIndex">The projection shard.</param>
    /// <param name="shardCount">The shard count the cursors were written under.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The oldest outstanding receipt time, or null when nothing is outstanding.</returns>
    Task<DateTimeOffset?> GetOldestUnprojectedReceiptAsync(
        long cursor,
        DateTimeOffset streamingWindow,
        DateTimeOffset visibilityCutoff,
        int shardIndex,
        int shardCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the projection cursor for the given shard, inserting a row when none exists or
    /// advancing the stored position and update timestamp otherwise. Records the shard count in
    /// effect so a later shard-count change can be detected and refused at startup.
    /// </summary>
    /// <param name="shardIndex">The projection shard's zero-based index.</param>
    /// <param name="position">The last <see cref="Models.MachineTelemetry.Id"/> this shard has projected.</param>
    /// <param name="shardCount">The total shard count in effect for this cursor.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task SetProjectionCursorAsync(int shardIndex, long position, int shardCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the shard count recorded on any persisted cursor, or <see langword="null"/> when no
    /// cursor rows exist yet (a first-ever start). Used by the streaming service to refuse to run when
    /// the configured shard count differs from the one the cursors were written under.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<int?> GetPersistedShardCountAsync(CancellationToken cancellationToken = default);
}
