// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Indexes data export jobs by tenant and request time. Every export request reads the tenant's
/// most recent request to evaluate the per-tier cooldown, and the table had no index at all —
/// rows are never deleted, only expired in place, so that read degrades into a growing sequential
/// scan. Also serves the check for an already-running export.
/// </summary>
[MigrationVersion(2026, 08, 27, 1)]
public sealed class AddDataExportJobsTenantIndex : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        Create.Index("IX_DataExportJobs_TenantId_RequestedAt")
            .OnTable(TableNames.DataExportJobs)
            .OnColumn("TenantId").Ascending()
            .OnColumn("RequestedAt").Descending();
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Delete.Index("IX_DataExportJobs_TenantId_RequestedAt").OnTable(TableNames.DataExportJobs);
    }
}
