// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using LinqToDB.Mapping;

namespace Framlux.FleetManagement.Database.Models;

/// <summary>
/// The stream cursor for the Kubernetes inventory projection. The table holds a single row, whose
/// <see cref="Id"/> is always 1, recording the largest <see cref="MachineTelemetry.Id"/> the
/// projection has already applied so it resumes from there after a restart. This is internal
/// worker bookkeeping, not an admin-facing configuration setting.
/// </summary>
[Table(TableNames.KubernetesInventoryProjectionCursor)]
public sealed class KubernetesInventoryProjectionCursor
{
    /// <summary>The row key. The projection is a single stream, so this is always 1.</summary>
    [PrimaryKey]
    [Column("Id"), NotNull]
    public int Id { get; set; }

    /// <summary>The last <see cref="MachineTelemetry.Id"/> the projection has applied or skipped.</summary>
    [Column("Position"), NotNull]
    public long Position { get; set; }

    /// <summary>When the cursor was last advanced.</summary>
    [Column("UpdatedAt"), NotNull]
    public DateTimeOffset UpdatedAt { get; set; }
}
