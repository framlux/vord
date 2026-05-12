// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Services.Core.Infrastructure;

/// <summary>
/// Defines the partitioned tables and their partition key columns for the partition management service.
/// Table names must match the constants in the database project's TableNames class.
/// </summary>
internal static class PartitionedTableConfig
{
    /// <summary>
    /// The set of tables that are range-partitioned by a timestamp column on PostgreSQL.
    /// Each entry maps a table name to its partition key column.
    /// </summary>
    internal static readonly IReadOnlyList<PartitionedTable> Tables =
    [
        new("MachineTelemetry", "ReceivedAt"),
        new("AuditLog", "Timestamp"),
        new("AlertEvents", "TriggeredAt"),
        new("RemoteCommands", "CreatedAt"),
    ];

    /// <summary>
    /// Describes a table that is range-partitioned by a timestamp column.
    /// </summary>
    /// <param name="TableName">The name of the partitioned parent table.</param>
    /// <param name="PartitionColumn">The timestamp column used as the range partition key.</param>
    internal sealed record PartitionedTable(string TableName, string PartitionColumn);
}
