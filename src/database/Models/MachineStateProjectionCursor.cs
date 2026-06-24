// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// Per-shard stream cursor for the telemetry-to-state projection. Each row records the largest
/// <see cref="MachineTelemetry.Id"/> a projection shard has already projected into machine-state,
/// so the worker resumes from where it left off after a restart. This is internal worker
/// bookkeeping, not an admin-facing configuration setting.
/// </summary>
[Table(TableNames.MachineStateProjectionCursor)]
public sealed class MachineStateProjectionCursor
{
    /// <summary>
    /// The projection shard's zero-based index under modulo partitioning.
    /// </summary>
    [PrimaryKey]
    [Column("ShardIndex"), NotNull]
    public int ShardIndex { get; set; }

    /// <summary>
    /// The last <see cref="MachineTelemetry.Id"/> this shard has projected into machine-state.
    /// </summary>
    [Column("Position"), NotNull]
    public long Position { get; set; }

    /// <summary>
    /// When this cursor was last advanced.
    /// </summary>
    [Column("UpdatedAt"), NotNull]
    public DateTimeOffset UpdatedAt { get; set; }
}
