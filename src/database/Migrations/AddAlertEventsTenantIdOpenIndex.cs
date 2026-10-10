// Copyright (c) 2026 Framlux LLC
// Licensed under the Functional Source License, Version 1.1, ALv2 Future License
// See LICENSE for details.

using FluentMigrator;

namespace Framlux.FleetManagement.Database.Migrations;

/// <summary>
/// Indexes the tenants that still have an unresolved alert event. Alert evaluation asks, once a
/// minute, which tenants have an open event, and neither existing index leads with the status, so
/// on the range-partitioned events table that question scanned every daily partition. Resolved
/// events are the overwhelming majority of the table, so the predicate keeps them out of the index.
/// </summary>
[MigrationVersion(2026, 10, 07, 2)]
public sealed class AddAlertEventsTenantIdOpenIndex : Migration
{
    /// <inheritdoc/>
    public override void Up()
    {
        // The predicate is the stored value of AlertEventStatus.Resolved (3), and it must stay
        // textually identical to the query's "Status <> 3" for the planner to use the index.
        // FluentMigrator's fluent index builder has no partial-index support, so both dialects are
        // raw SQL. On the partitioned parent Postgres builds the index on every existing partition
        // and gives each new daily partition its own copy.
        IfDatabase("PostgreSQL").Execute.Sql("""
            CREATE INDEX IF NOT EXISTS "IX_AlertEvents_TenantId_Open"
            ON "AlertEvents" ("TenantId") WHERE "Status" <> 3
            """);

        IfDatabase("SQLite").Execute.Sql("""
            CREATE INDEX IF NOT EXISTS "IX_AlertEvents_TenantId_Open"
            ON "AlertEvents" ("TenantId") WHERE "Status" <> 3
            """);
    }

    /// <inheritdoc/>
    public override void Down()
    {
        IfDatabase("PostgreSQL").Execute.Sql("""DROP INDEX IF EXISTS "IX_AlertEvents_TenantId_Open" """);

        IfDatabase("SQLite").Execute.Sql("""DROP INDEX IF EXISTS "IX_AlertEvents_TenantId_Open" """);
    }
}
