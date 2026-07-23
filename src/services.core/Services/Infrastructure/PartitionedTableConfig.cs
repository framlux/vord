// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using Framlux.FleetManagement.Database;

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// Defines the partitioned tables the partition management job maintains. Simple daily-range tables
/// are listed in <see cref="RangeTables"/>; <c>MachineTelemetry</c> is handled separately because it
/// is composite — partitioned LIST by <see cref="RetentionClass"/> and then RANGE by day — so its
/// daily leaves live under a per-class parent and are dropped on each class's own schedule. Table
/// names must match the constants in the database project's TableNames class.
/// </summary>
internal static class PartitionedTableConfig
{
    /// <summary>
    /// The simple daily-range-partitioned tables, each mapping a table name to its partition key
    /// column. Their leaves are dropped at the maximum (Long-class) retention window.
    /// </summary>
    internal static readonly IReadOnlyList<PartitionedTable> RangeTables =
    [
        new("AuditLog", "Timestamp"),
        new("AlertEvents", "TriggeredAt"),
        new("RemoteCommands", "CreatedAt"),
    ];

    /// <summary>
    /// The retention classes <c>MachineTelemetry</c> is partitioned into. Each class owns its own set
    /// of daily leaf partitions and is created and dropped on its own schedule.
    /// </summary>
    internal static readonly IReadOnlyList<RetentionClass> TelemetryRetentionClasses =
    [
        RetentionClass.Short,
        RetentionClass.Medium,
        RetentionClass.Long,
    ];

    /// <summary>
    /// Describes a table that is range-partitioned by a timestamp column.
    /// </summary>
    /// <param name="TableName">The name of the partitioned parent table.</param>
    /// <param name="PartitionColumn">The timestamp column used as the range partition key.</param>
    internal sealed record PartitionedTable(string TableName, string PartitionColumn);
}
