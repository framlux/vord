// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Initial database migration that creates core tables for machine management and user accounts.
/// </summary>
[MigrationVersion(2026, 04, 05, 2)]
public sealed class InitialMigration2 : Migration
{
    /// <summary>
    /// Applies the migration by creating initial database tables and indexes.
    /// </summary>
    public override void Up()
    {
        // In order to create our System account, we need to insert it before we add the foreign key constraints
        // otherwise the CreatedByUserId constraint will fail.
        Create.ForeignKey("FK_Users_CreatedBy")
            .FromTable(TableNames.Users).ForeignColumn("CreatedByUserId")
            .ToTable(TableNames.Users).PrimaryColumn("Id");
        Create.ForeignKey("FK_Users_DeletedBy")
            .FromTable(TableNames.Users).ForeignColumn("DeletedByUserId")
            .ToTable(TableNames.Users).PrimaryColumn("Id");
    }

    /// <summary>
    /// Reverts the migration by dropping all initial tables and indexes.
    /// </summary>
    public override void Down()
    {
        Delete.ForeignKey("FK_Users_CreatedBy")
            .OnTable(TableNames.Users);
        Delete.ForeignKey("FK_Users_DeletedBy")
            .OnTable(TableNames.Users);
    }
}
