// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Creates the dedicated "hangfire" Postgres schema and lets Hangfire.PostgreSql install its tables
/// via its embedded installer script. Skipped on SQLite (functional tests do not run the Hangfire server).
/// </summary>
[MigrationVersion(2026, 05, 16, 1)]
public sealed class HangfireSchemaMigration : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        IfDatabase("PostgreSQL").Execute.Sql(@"CREATE SCHEMA IF NOT EXISTS ""hangfire"";");
    }

    /// <inheritdoc/>
    public override void Down()
    {
        IfDatabase("PostgreSQL").Execute.Sql(@"DROP SCHEMA IF EXISTS ""hangfire"" CASCADE;");
    }
}
