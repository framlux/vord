// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

namespace Framlux.FleetManagement.Database.Repositories;

/// <summary>
/// Repository for the daily-partition maintenance operations performed by
/// PartitionManagementJob. Encapsulates the DDL surface (CREATE / DROP partition table) and the
/// retention-policy query so the job does not depend on <c>DatabaseContext</c> directly.
/// </summary>
public interface IPartitionRepository
{
    /// <summary>
    /// Returns the maximum <c>RetentionDays</c> across all tier feature limit rows, or
    /// <see langword="null"/> if the table is empty.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int?> GetMaxRetentionDaysAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Executes a partition-management DDL statement. The statement is composed by the caller
    /// (CREATE TABLE … PARTITION OF or DROP TABLE IF EXISTS) and contains only data composed from
    /// the schema-defined table list — no user input is interpolated.
    /// </summary>
    /// <param name="sql">The DDL SQL statement.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExecutePartitionDdlAsync(string sql, CancellationToken cancellationToken);
}
