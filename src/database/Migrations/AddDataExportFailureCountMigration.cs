// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Adds <c>FailureCount</c> (int, NOT NULL, default = 0) to <c>DataExportJobs</c>.
/// <c>DataExportProcessingJob</c> increments this column on each processing failure and
/// transitions the row to <see cref="Enums.DataExportJobStatus.Failed"/> after a small retry
/// budget so a permanently broken job stops generating one Failed Hangfire entry per minute.
/// </summary>
[MigrationVersion(2026, 05, 20, 2)]
public sealed class AddDataExportFailureCountMigration : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        Alter.Table(TableNames.DataExportJobs)
            .AddColumn("FailureCount").AsInt32().NotNullable().WithDefaultValue(0);
    }

    /// <inheritdoc/>
    public override void Down()
    {
        Delete.Column("FailureCount").FromTable(TableNames.DataExportJobs);
    }
}
