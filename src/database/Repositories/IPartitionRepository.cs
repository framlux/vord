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
    /// Returns the drop window, in days, of the Long retention class: the greater of the fixed
    /// 365-day floor and the largest per-tenant retention override. A rare over-365-day override
    /// extends only the Long class; the Short and Medium windows are fixed constants and are never
    /// stretched by any tenant's override.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<int> GetLongClassRetentionDaysAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Executes a partition-management DDL statement. The statement is composed by the caller
    /// (CREATE TABLE … PARTITION OF or DROP TABLE IF EXISTS) and contains only data composed from
    /// the schema-defined table list — no user input is interpolated.
    /// </summary>
    /// <param name="sql">The DDL SQL statement.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExecutePartitionDdlAsync(string sql, CancellationToken cancellationToken);
}
