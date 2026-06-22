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
        // The System account is inserted by InitialMigration before these self-referential foreign
        // keys are created, so CreatedByUserId = 1 already resolves when the constraints are added.
        // On Postgres the constraints are DEFERRABLE INITIALLY DEFERRED: the System row references
        // itself, which makes a --data-only restore otherwise require disabling triggers (pg_dump
        // emits a circular-foreign-key warning for exactly this). Deferring constraint checks to
        // commit time lets such a restore load the row cleanly. FluentMigrator has no fluent option
        // for DEFERRABLE, so the Postgres path is expressed as raw SQL.
        IfDatabase("PostgreSQL").Execute.Sql(@"
            ALTER TABLE ""UserAccounts""
            ADD CONSTRAINT ""FK_Users_CreatedBy""
            FOREIGN KEY (""CreatedByUserId"") REFERENCES ""UserAccounts"" (""Id"")
            DEFERRABLE INITIALLY DEFERRED;");
        IfDatabase("PostgreSQL").Execute.Sql(@"
            ALTER TABLE ""UserAccounts""
            ADD CONSTRAINT ""FK_Users_DeletedBy""
            FOREIGN KEY (""DeletedByUserId"") REFERENCES ""UserAccounts"" (""Id"")
            DEFERRABLE INITIALLY DEFERRED;");

        // SQLite (the in-memory test database) cannot ALTER a table to add a foreign key with
        // explicit deferral, and its deferral semantics are not relevant to backups; the fluent
        // self-referential keys preserve the historical test-schema behavior.
        IfDatabase("SQLite").Create.ForeignKey("FK_Users_CreatedBy")
            .FromTable(TableNames.Users).ForeignColumn("CreatedByUserId")
            .ToTable(TableNames.Users).PrimaryColumn("Id");
        IfDatabase("SQLite").Create.ForeignKey("FK_Users_DeletedBy")
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
